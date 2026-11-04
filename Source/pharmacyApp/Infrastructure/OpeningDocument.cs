using System.IO;
using Markdig;
using Markdig.Syntax;

namespace pharmacyApp.Infrastructure;

public sealed record OpeningPage(string Title, string Html);

public static class OpeningDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().UseAutoLinks().UseEmphasisExtras().DisableHtml().Build();

    public static IReadOnlyList<OpeningPage> Load()
    {
        var path = Path.Combine(AppPaths.RootDirectory, "opening.md");
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
        return Parse(File.ReadAllText(path));
    }

    public static IReadOnlyList<OpeningPage> Parse(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var pages = new List<OpeningPage>();
        var start = 0;
        var title = "Opening";
        foreach (var heading in document.OfType<HeadingBlock>().Where(heading => heading.Level == 1))
        {
            if (heading.Span.Start > start && !string.IsNullOrWhiteSpace(markdown[start..heading.Span.Start]))
                pages.Add(Render(title, markdown[start..heading.Span.Start]));
            title = Markdown.ToPlainText(markdown[heading.Span.Start..(heading.Span.End + 1)], Pipeline).Trim();
            if (string.IsNullOrWhiteSpace(title)) title = "Opening";
            start = heading.Span.Start;
        }
        if (start < markdown.Length || pages.Count == 0)
            pages.Add(Render(title, markdown[start..]));
        return pages;
    }

    private static OpeningPage Render(string title, string markdown) => new(title,
        "<!doctype html><html><head><meta charset='utf-8'>" +
        "<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:;\">" +
        "<style>" + Css + "</style></head><body><article class='markdown-body'>" +
        Markdown.ToHtml(markdown, Pipeline) + "</article></body></html>");

    private const string Css = """
        :root { color-scheme: light; }
        body { margin:0; background:#fff; color:#1f2328; font:16px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif; }
        .markdown-body { max-width:980px; padding:32px; margin:auto; overflow-wrap:break-word; }
        h1,h2,h3,h4,h5,h6 { margin:24px 0 16px; font-weight:600; line-height:1.25; }
        h1 { font-size:2em; } h2 { font-size:1.5em; } h3 { font-size:1.25em; }
        h1,h2 { padding-bottom:.3em; border-bottom:1px solid #d1d9e0; }
        article>:first-child { margin-top:0; } p,ul,ol,blockquote,pre,table { margin:0 0 16px; }
        ul,ol { padding-left:2em; } li+li { margin-top:.25em; } li>p { margin-top:16px; }
        li>ul,li>ol { margin-bottom:0; } input { vertical-align:middle; }
        hr { height:4px; padding:0; margin:24px 0; background:#d1d9e0; border:0; }
        table { border-collapse:collapse; display:block; max-width:100%; overflow:auto; }
        th,td { padding:6px 13px; border:1px solid #d1d9e0; } th { font-weight:600; }
        tr:nth-child(2n) { background:#f6f8fa; }
        blockquote { padding:0 1em; color:#59636e; border-left:4px solid #d1d9e0; }
        a { color:#0969da; text-decoration:none; } a:hover { text-decoration:underline; }
        code,pre { font:85%/1.45 Consolas,"Courier New",monospace; }
        code { padding:.2em .4em; background:#eff1f3; } pre { padding:16px; overflow:auto; background:#f6f8fa; }
        pre code { padding:0; background:transparent; } img { max-width:100%; }
        """;
}
