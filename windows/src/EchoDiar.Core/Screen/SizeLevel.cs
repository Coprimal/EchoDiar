namespace EchoDiar.Core.Screen;

/// <summary>
/// How large a conversation's files are, for the tint of its size in the library (macOS <c>SizeLevel</c> in
/// ConversationCard.swift): normal below 50 MB (an hour of audio), large (yellow) up to 200 MB, larger (orange) up to
/// 1 GB (an hour with a screen recording), huge (red) from 1 GB. Decimal units, as the size text shows them.
/// </summary>
public static class SizeLevel
{
    public enum Level { Normal, Large, Larger, Huge }

    public static Level For(long bytes) => bytes switch
    {
        < 50_000_000 => Level.Normal,
        < 200_000_000 => Level.Large,
        < 1_000_000_000 => Level.Larger,
        _ => Level.Huge,
    };
}
