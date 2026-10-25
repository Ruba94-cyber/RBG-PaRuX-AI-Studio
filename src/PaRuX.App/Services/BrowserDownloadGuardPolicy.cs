namespace PaRuX.Services
{
    /// <summary>
    /// Session-wide download denial for the developer browser. A web page must not
    /// write files into the user's download folder, which lies outside the private
    /// browser profile. The command is sent on the page socket during session setup,
    /// before any navigation. Browser.setDownloadBehavior is accepted there; this was
    /// verified against Chromium-based Edge. Navigation replies that still report
    /// isDownload are treated as failures by BrowserDevToolsNavigationResultPolicy.
    /// </summary>
    public static class BrowserDownloadGuardPolicy
    {
        public static (string Method, object Parameters) DenyAllDownloads() =>
            ("Browser.setDownloadBehavior", new { behavior = "deny" });
    }
}
