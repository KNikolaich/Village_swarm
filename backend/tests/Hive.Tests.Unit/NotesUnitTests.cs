using Hive.Modules.Notes;

namespace Hive.Tests.Unit;

public class NotesUnitTests
{
    [Fact]
    public void Pictures_are_recognised_by_their_bytes_not_by_their_name()
    {
        Assert.Equal("image/png", NoteAttachmentService.Sniff([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]));
        Assert.Equal("image/jpeg", NoteAttachmentService.Sniff([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/webp", NoteAttachmentService.Sniff("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(NoteAttachmentService.Sniff("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
    }

    [Fact]
    public void File_names_lose_folders_and_control_characters()
    {
        Assert.Equal("passwd", NoteAttachmentService.CleanName("../../etc/passwd"));
        Assert.Equal("план.txt", NoteAttachmentService.CleanName("C:\\Users\\k\\план.txt"));
        Assert.Equal("ab.png", NoteAttachmentService.CleanName("a\"\nb.png"));
        Assert.Equal("file", NoteAttachmentService.CleanName("  "));
    }

    [Fact]
    public void A_search_snippet_shows_the_text_around_the_match()
    {
        var body = new string('x', 200) + " починить OTA на камере " + new string('y', 200);
        var snippet = NoteService.Snippet(body, "ota");
        Assert.StartsWith("…", snippet);
        Assert.Contains("починить OTA", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Equal("коротко", NoteService.Snippet("коротко", "нет"));
    }
}
