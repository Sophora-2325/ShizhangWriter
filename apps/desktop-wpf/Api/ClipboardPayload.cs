using System.Text;

namespace NovelGm.Desktop.Api;

/// <summary>
/// 剪贴板里的 HTML 必须按 Windows 的 CF_HTML 格式包一层：前面有一段写明各段字节偏移的头部，
/// 少了它，浏览器和富文本编辑器只会把它当纯文本粘进来（加粗、引用全丢）。
///
/// 头部长度是固定的（偏移量一律补成 10 位），所以先用占位值量一次长度，再回填真实的偏移。
/// 这里刻意做成纯函数，方便人工核对偏移算得对不对。
/// </summary>
public static class ClipboardPayload
{
    private const string HeaderTemplate =
        "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";

    private const string BeforeFragment = "<html><body><!--StartFragment-->";
    private const string AfterFragment = "<!--EndFragment--></body></html>";

    /// <summary>把一段 HTML 片段包成 CF_HTML。</summary>
    public static string BuildCfHtml(string? fragment)
    {
        var body = fragment ?? "";
        // 偏移一律补成 10 位，所以头部长度在回填前后一致：先量一次长度就够了
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(HeaderTemplate, 0, 0, 0, 0));
        var startHtml = headerLength;  // 指向 <html>，规范里要求这里给出文档起点
        var startFragment = headerLength + Encoding.UTF8.GetByteCount(BeforeFragment);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(body);
        var endHtml = headerLength + Encoding.UTF8.GetByteCount(BeforeFragment + body + AfterFragment);
        return string.Format(HeaderTemplate, startHtml, endHtml, startFragment, endFragment)
               + BeforeFragment + body + AfterFragment;
    }
}
