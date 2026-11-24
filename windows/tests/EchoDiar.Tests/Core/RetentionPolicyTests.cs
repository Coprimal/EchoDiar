using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.Core;

// Port of macOS RetentionPolicyTests: guards auto-delete against data loss. The policy cases are pure (no file is read
// or deleted); the last two run LibraryStore.ApplyRetentionAsync on real folders.
public sealed class RetentionPolicyTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));

    public RetentionPolicyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SelectsOnlyConversationsOlderThanTheAge()
    {
        var old = Make(ageDays: 31);
        var recent = Make(ageDays: 29);

        var deletions = RetentionPolicy.Select(
            [old, recent], Now, Settings(RetentionMode.WholeConversation, 30), Audio, _ => true);

        var deletion = Assert.Single(deletions);
        Assert.Equal(old.Id, deletion.ConversationId);
        Assert.Equal([old.Folder], deletion.Items);
    }

    // Audio only trashes just what audioFiles lists (tested on a real folder in LibraryStoreTests), never the folder;
    // a conversation with no audio left is skipped.
    [Fact]
    public void AudioOnlyTrashesOnlyTheListedAudioAndSkipsConversationsWithout()
    {
        var withAudio = Make(ageDays: 120);
        var withoutAudio = Make(ageDays: 120);

        var deletions = RetentionPolicy.Select(
            [withAudio, withoutAudio], Now, Settings(RetentionMode.AudioOnly, 90),
            c => c.Id == withAudio.Id ? Audio(c) : [], _ => true);

        var deletion = Assert.Single(deletions);
        Assert.Equal(withAudio.Id, deletion.ConversationId);
        Assert.Equal(RetentionMode.AudioOnly, deletion.Mode);
        Assert.Equal(Audio(withAudio), deletion.Items);
    }

    // Audio and video (AudioOnly) lists the audio then the video files, so a conversation with video alone is selected;
    // video only lists just the video files and skips a conversation without, its audio stays.
    [Fact]
    public void AudioOnlyListsAudioAndVideoAndVideoOnlyJustVideo()
    {
        var both = Make(ageDays: 120);
        var onlyVideo = Make(ageDays: 120);
        var onlyAudio = Make(ageDays: 120);
        IReadOnlyList<string> AudioOf(Conversation c) => c.Id == onlyVideo.Id ? [] : Audio(c);
        IReadOnlyList<string> VideoOf(Conversation c) => c.Id == onlyAudio.Id ? [] : Video(c);

        var audioAndVideo = RetentionPolicy.Select(
            [both, onlyVideo, onlyAudio], Now, Settings(RetentionMode.AudioOnly, 90), AudioOf, _ => true, VideoOf);
        var videoOnly = RetentionPolicy.Select(
            [both, onlyVideo, onlyAudio], Now, Settings(RetentionMode.VideoOnly, 90), AudioOf, _ => true, VideoOf);

        Assert.Equal([both.Id, onlyVideo.Id, onlyAudio.Id], audioAndVideo.Select(d => d.ConversationId));
        Assert.All(audioAndVideo, d => Assert.Equal(RetentionMode.AudioOnly, d.Mode));
        Assert.Equal([.. Audio(both), .. Video(both)], audioAndVideo[0].Items);
        Assert.Equal(Video(onlyVideo), audioAndVideo[1].Items);
        Assert.Equal(Audio(onlyAudio), audioAndVideo[2].Items);
        Assert.Equal([both.Id, onlyVideo.Id], videoOnly.Select(d => d.ConversationId));
        Assert.All(videoOnly, d => Assert.Equal(RetentionMode.VideoOnly, d.Mode));
        Assert.Equal(Video(both), videoOnly[0].Items);
        Assert.Equal(Video(onlyVideo), videoOnly[1].Items);
    }

    // How much a mode deletes; the enum order cannot tell (VideoOnly is appended last).
    [Fact]
    public void RanksOrderVideoOnlyBelowAudioOnlyBelowWholeConversation()
    {
        Assert.True(RetentionMode.VideoOnly.Rank() < RetentionMode.AudioOnly.Rank());
        Assert.True(RetentionMode.AudioOnly.Rank() < RetentionMode.WholeConversation.Rank());
    }

    [Theory]
    [InlineData(ConversationStatus.Recording)]
    [InlineData(ConversationStatus.Compressing)]
    [InlineData(ConversationStatus.Transcribing)]
    [InlineData(ConversationStatus.Ready)]
    [InlineData(ConversationStatus.Recovered)]
    [InlineData(ConversationStatus.CompressionFailed)]
    [InlineData(ConversationStatus.TranscriptionFailed)]
    [InlineData(ConversationStatus.Failed)]
    [InlineData(ConversationStatus.Unknown)]
    public void BusyOrUntranscribedConversationsAreNeverSelected(ConversationStatus status)
    {
        var old = Make(ageDays: 400, status: status);
        foreach (var mode in new[] { RetentionMode.AudioOnly, RetentionMode.WholeConversation })
        {
            Assert.Empty(RetentionPolicy.Select([old], Now, Settings(mode, 1), Audio, _ => true));
        }
    }

    [Fact]
    public void TranscribedStatusWithoutTranscriptIsNeverSelected()
    {
        var old = Make(ageDays: 400, hasTranscript: false);
        Assert.Empty(RetentionPolicy.Select([old], Now, Settings(RetentionMode.WholeConversation, 1), Audio, _ => true));
    }

    // conversation.json may come from another device (sync): marked transcribed but without transcript.json on disk
    // (a partial sync, a forged file), the conversation is kept; with the file, it goes.
    [Theory]
    [InlineData(RetentionMode.WholeConversation)]
    [InlineData(RetentionMode.AudioOnly)]
    public void TranscribedWithoutTranscriptFileOnDiskIsNeverSelected(RetentionMode mode)
    {
        var old = Make(ageDays: 400);
        Directory.CreateDirectory(old.Folder);

        Assert.Empty(RetentionPolicy.Select([old], Now, Settings(mode, 1), Audio));

        File.WriteAllText(Path.Combine(old.Folder, Transcript.FileName), "{}");
        Assert.Equal([old.Id], RetentionPolicy.Select([old], Now, Settings(mode, 1), Audio).Select(d => d.ConversationId));
    }

    // macOS applyRetention: a conversation with an active job (isBusy) keeps its files, the others go.
    [Fact]
    public async Task ApplyRetentionSkipsBusyConversations()
    {
        var busy = Make(ageDays: 120);
        var idle = Make(ageDays: 120);
        foreach (var conversation in new[] { busy, idle })
        {
            Directory.CreateDirectory(conversation.Folder);
            conversation.Write();
            File.WriteAllText(Path.Combine(conversation.Folder, "audio.m4a"), "x");
            File.WriteAllText(Path.Combine(conversation.Folder, "screen.mp4"), "x");
            File.WriteAllText(Path.Combine(conversation.Folder, Transcript.FileName), "{}");
        }
        var store = new LibraryStore(_dir, new DeletingTrash());

        var done = await store.ApplyRetentionAsync(
            Settings(RetentionMode.AudioOnly, 90), id => Task.FromResult(id == busy.Id), Now);

        Assert.Equal([idle.Id], done.Select(d => d.ConversationId));
        Assert.True(File.Exists(Path.Combine(busy.Folder, "audio.m4a")));
        Assert.False(File.Exists(Path.Combine(idle.Folder, "audio.m4a")));
        Assert.True(File.Exists(Path.Combine(busy.Folder, "screen.mp4")));
        Assert.False(File.Exists(Path.Combine(idle.Folder, "screen.mp4")));
        Assert.False(store.Conversations.Single(c => c.Id == idle.Id).HasAudio);
    }

    // Regression: ApplyRetentionAsync sent every mode but WholeConversation to DeleteAudioAsync, so video only would
    // have trashed the audio. It trashes just the video files; the audio and the transcript stay.
    [Fact]
    public async Task VideoOnlyDeletionNeverRemovesAudio()
    {
        var conversation = Make(ageDays: 120);
        Directory.CreateDirectory(conversation.Folder);
        conversation.Write();
        foreach (var name in new[] { "audio.m4a", "screen.mp4", "screen.mov", Transcript.FileName })
        {
            File.WriteAllText(Path.Combine(conversation.Folder, name), "x");
        }
        var store = new LibraryStore(_dir, new DeletingTrash());

        var done = await store.ApplyRetentionAsync(Settings(RetentionMode.VideoOnly, 90), _ => Task.FromResult(false), Now);

        Assert.Equal([conversation.Id], done.Select(d => d.ConversationId));
        Assert.True(File.Exists(Path.Combine(conversation.Folder, "audio.m4a")));
        Assert.True(File.Exists(Path.Combine(conversation.Folder, Transcript.FileName)));
        Assert.False(File.Exists(Path.Combine(conversation.Folder, "screen.mp4")));
        Assert.False(File.Exists(Path.Combine(conversation.Folder, "screen.mov")));
        Assert.True(store.Conversations.Single().HasAudio);
    }

    private static AppSettings Settings(RetentionMode mode, int days) =>
        new() { AutoDeleteEnabled = true, AutoDeleteAfterDays = days, AutoDeleteMode = mode };

    private Conversation Make(double ageDays, ConversationStatus status = ConversationStatus.Transcribed, bool hasTranscript = true)
    {
        var id = Guid.NewGuid();
        return new Conversation
        {
            Id = id, CreatedAt = Now.AddDays(-ageDays), Status = status, HasTranscript = hasTranscript, HasSummary = true,
            Folder = Path.Combine(_dir, id.ToString()),
        };
    }

    /// <summary>The audio files the policy is told the folder holds.</summary>
    private static IReadOnlyList<string> Audio(Conversation conversation) =>
        [Path.Combine(conversation.Folder, "audio.m4a"), Path.Combine(conversation.Folder, "audio.caf")];

    /// <summary>The video files the policy is told the folder holds.</summary>
    private static IReadOnlyList<string> Video(Conversation conversation) =>
        [Path.Combine(conversation.Folder, "screen.mp4")];

    private sealed class DeletingTrash : IFileTrash
    {
        public void MoveToTrash(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
    }
}
