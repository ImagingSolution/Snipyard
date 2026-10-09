namespace Snipyard.Services;

/// <summary>
/// A rough token count for text Snipyard can see but never sends - CLAUDE.md, skill
/// descriptions - so the extensions panel can rank what each one adds to every turn.
///
/// About four ASCII characters make a token; a Japanese character is close to one on its
/// own, so counting it as a quarter would hide exactly the files that cost the most here.
/// Good enough to sort by and to compare against /context, not to bill by.
/// </summary>
public static class TokenEstimate
{
    public static long Of(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        long ascii = 0, other = 0;
        foreach (var c in text)
        {
            if (c < 0x80) ascii++;
            else if (!char.IsLowSurrogate(c)) other++;
        }
        return (ascii + 3) / 4 + other;
    }
}
