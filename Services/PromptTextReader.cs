using System.IO.Compression;
using System.IO;
using System.Xml.Linq;

namespace FrameTrace.Editor.Services;

public static class PromptTextReader
{
    private static readonly XNamespace WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static string Read(FileInfo file)
    {
        return string.Equals(file.Extension, ".docx", StringComparison.OrdinalIgnoreCase)
            ? ReadDocx(file.FullName)
            : File.ReadAllText(file.FullName);
    }

    private static string ReadDocx(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("DOCX 文档中缺少正文内容。" );
        using var documentStream = documentEntry.Open();
        var document = XDocument.Load(documentStream);

        var paragraphs = document
            .Descendants(WordNamespace + "p")
            .Select(ReadParagraph)
            .ToArray();

        return string.Join(Environment.NewLine, paragraphs).Trim();
    }

    private static string ReadParagraph(XElement paragraph)
    {
        return string.Concat(paragraph.Descendants().Select(node => node.Name.LocalName switch
        {
            "t" => node.Value,
            "tab" => "\t",
            "br" or "cr" => Environment.NewLine,
            _ => string.Empty
        }));
    }
}