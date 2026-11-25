using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;

namespace EchoDiar.Core;

/// <summary>
/// Swift <c>RetentionPolicy</c>: which conversations auto-delete moves to the Recycle Bin. Pure: it deletes nothing
/// (<see cref="LibraryStore"/> does) and ignores <see cref="AppSettings.AutoDeleteEnabled"/>, so the Storage page can
/// preview before the user turns auto-delete on.
/// </summary>
public static class RetentionPolicy
{
    /// <summary>
    /// What goes to the Recycle Bin: the conversation folder (whole conversation), its audio and video files (audio and
    /// video, <see cref="RetentionMode.AudioOnly"/>) or its video files (video only).
    /// </summary>
    public sealed record Deletion(Guid ConversationId, RetentionMode Mode, IReadOnlyList<string> Items);

    /// <summary>
    /// Transcribed conversations created more than <see cref="AppSettings.AutoDeleteAfterDays"/> days before
    /// <paramref name="now"/>. Never selects a conversation that is recording, compressing, transcribing, failed or not
    /// yet transcribed, nor one whose transcript file is missing from disk (<paramref name="hasTranscriptFile"/>, default
    /// <see cref="LibraryStore.HasTranscriptFile"/>): conversation.json may come from another device through sync, or a
    /// partial sync. Audio and video (<see cref="RetentionMode.AudioOnly"/>): lists the conversation's
    /// <paramref name="audioFiles"/> (default <see cref="LibraryStore.AudioFiles"/>) then its <paramref name="videoFiles"/>
    /// (default <see cref="LibraryStore.VideoFiles"/>), and skips it when both are empty. Video only: lists just its
    /// <paramref name="videoFiles"/>, and skips it when it has none left.
    /// </summary>
    public static IReadOnlyList<Deletion> Select(
        IEnumerable<Conversation> conversations, DateTimeOffset now, AppSettings settings,
        Func<Conversation, IReadOnlyList<string>>? audioFiles = null, Func<Conversation, bool>? hasTranscriptFile = null,
        Func<Conversation, IReadOnlyList<string>>? videoFiles = null)
    {
        audioFiles ??= c => LibraryStore.AudioFiles(c.Folder);
        videoFiles ??= c => LibraryStore.VideoFiles(c.Folder);
        hasTranscriptFile ??= c => LibraryStore.HasTranscriptFile(c.Folder);
        var days = AppSettings.Clamp(settings.AutoDeleteAfterDays, AppSettings.AutoDeleteDaysRange);
        var cutoff = now.AddSeconds(-(double)days * 86_400);
        List<Deletion> deletions = [];
        foreach (var conversation in conversations)
        {
            if (conversation.Status != ConversationStatus.Transcribed || !conversation.HasTranscript
                || conversation.CreatedAt >= cutoff || !hasTranscriptFile(conversation))
            {
                continue;
            }
            switch (settings.AutoDeleteMode)
            {
                case RetentionMode.WholeConversation:
                    deletions.Add(new Deletion(conversation.Id, RetentionMode.WholeConversation, [conversation.Folder]));
                    break;
                case RetentionMode.AudioOnly:
                    List<string> files = [.. audioFiles(conversation), .. videoFiles(conversation)];
                    if (files.Count > 0) deletions.Add(new Deletion(conversation.Id, RetentionMode.AudioOnly, files));
                    break;
                case RetentionMode.VideoOnly:
                    var video = videoFiles(conversation);
                    if (video.Count > 0) deletions.Add(new Deletion(conversation.Id, RetentionMode.VideoOnly, video));
                    break;
            }
        }
        return deletions;
    }
}
