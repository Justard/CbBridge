using System.Text.RegularExpressions;

namespace CbBridge.Core.Application.Links;

public enum LinkKind { Unknown, Project, Category, Item }

public sealed record CodebeamerLink(LinkKind Kind, string? Id, string Explanation);

/// <summary>Reads what a pasted codebeamer link points to. Item wins over category wins over project.</summary>
public static partial class LinkParser
{
    public static CodebeamerLink Parse(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0)
            return new(LinkKind.Unknown, null, "Paste a /project/{id}, /category/{id} or /item/{id} link, or just an item ID.");

        if (OnlyDigits().IsMatch(s))
            return new(LinkKind.Item, s, $"Item {s} → GET /item/{s} and /item/{s}/references");

        var m = ItemRx().Match(s);
        if (m.Success)
        {
            var id = m.Groups[1].Value;
            return new(LinkKind.Item, id, $"Item link → GET /item/{id} and /item/{id}/references");
        }
        m = CategoryRx().Match(s);
        if (m.Success)
        {
            var id = m.Groups[1].Value;
            return new(LinkKind.Category, id, $"Category link → GET /category/{id}/outline");
        }
        m = ProjectRx().Match(s);
        if (m.Success)
        {
            var id = m.Groups[1].Value;
            return new(LinkKind.Project, id, $"Project link → GET /project/{id} and /project/{id}/categories");
        }
        return new(LinkKind.Unknown, null, "No /project/, /category/ or /item/ ID found in this link.");
    }

    [GeneratedRegex(@"^\d+$")] private static partial Regex OnlyDigits();
    [GeneratedRegex(@"/(?:item|issue)/(\d+)", RegexOptions.IgnoreCase)] private static partial Regex ItemRx();
    // Newer codebeamer UIs show trackers as /tracker/{id}; treated like a category here.
    [GeneratedRegex(@"/(?:category|tracker)/(\d+)", RegexOptions.IgnoreCase)] private static partial Regex CategoryRx();
    [GeneratedRegex(@"/project/(\d+)", RegexOptions.IgnoreCase)] private static partial Regex ProjectRx();
}
