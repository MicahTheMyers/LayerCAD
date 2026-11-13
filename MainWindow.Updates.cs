using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Windows;

namespace LayerCAD;

public partial class MainWindow
{
    private const string ReleaseRepository = "MicahTheMyers/LayerCAD";
    private static readonly HttpClient updateClient = new() { Timeout = TimeSpan.FromMinutes(8) };
    private bool checkingForUpdates;

    private sealed record Release([property: JsonPropertyName("tag_name")] string Tag, ReleaseAsset[] Assets);
    private sealed record ReleaseAsset(string Name, long Size, string? Digest);

    private async void CheckForUpdatesClick(object sender, RoutedEventArgs e) => await CheckForUpdates(manual: true);

    private async Task CheckForUpdates(bool manual)
    {
        if (checkingForUpdates)
            return;
        var assembly = typeof(MainWindow).Assembly;
        bool portable = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(item => item.Key == "UpdateBundle" && item.Value == "true");
        if (!portable || RuntimeInformation.ProcessArchitecture != Architecture.X64 || Environment.ProcessPath is not { } executable)
        {
            if (manual)
                SetStatus("Use the Windows x64 release from GitHub for in-app updates.");
            return;
        }

        checkingForUpdates = true;
        string? folder = null;
        try
        {
            if (manual)
                SetStatus("Checking GitHub for updates…");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{ReleaseRepository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("LayerCAD-Updater/1.0");
            using var response = await updateClient.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (manual)
                    SetStatus("GitHub has no published LayerCAD release yet.");
                return;
            }
            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<Release>();
            if (release?.Tag is not { } tag || !Version.TryParse(tag.TrimStart('v'), out var version) ||
                new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)) <= assembly.GetName().Version)
            {
                if (manual)
                    SetStatus("LayerCAD is up to date.");
                return;
            }
            string name = $"LayerCAD-{tag.TrimStart('v')}-win-x64.exe";
            var asset = release.Assets.FirstOrDefault(item => item.Name == name);
            if (asset?.Digest is not { } digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                digest.Length != 71 || asset.Size is <= 0 or > 500_000_000)
                throw new InvalidDataException("The release is missing a verifiable Windows executable.");
            if (!IsVisible || MessageBox.Show(this, $"Download and install LayerCAD {version} from GitHub?",
                "LayerCAD Update", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;
            folder = Path.Combine(Path.GetTempPath(), "LayerCAD-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string payload = Path.Combine(folder, "update.exe");
            SetStatus($"Downloading LayerCAD {version}…");
            using var download = await updateClient.GetAsync(
                $"https://github.com/{ReleaseRepository}/releases/download/{Uri.EscapeDataString(tag)}/{name}",
                HttpCompletionOption.ResponseHeadersRead);
            download.EnsureSuccessStatusCode();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await using (var input = await download.Content.ReadAsStreamAsync(deadline.Token))
            await using (var file = File.Create(payload))
            {
                byte[] buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    if (file.Length + count > asset.Size)
                        throw new InvalidDataException("The download exceeds the published size.");
                    await file.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                }
                if (file.Length != asset.Size)
                    throw new InvalidDataException("The download is incomplete.");
            }
            await using (var file = File.OpenRead(payload))
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, deadline.Token));
                if (!hash.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download does not match the GitHub SHA-256 digest.");
            }
            if (!IsVisible || SketchHost.Content is not null)
            {
                SetStatus("Finish or cancel your sketch, then check for updates again.");
                return;
            }
            using var script = new StreamReader(assembly.GetManifestResourceStream("LayerCAD.Updates.Install-Update.ps1")
                ?? throw new IOException("The update installer is missing."));
            string command = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(await script.ReadToEndAsync()));

            var helper = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", command })
                helper.ArgumentList.Add(arg);
            helper.Environment["LAYERCAD_UPDATE_PID"] = Environment.ProcessId.ToString();
            helper.Environment["LAYERCAD_UPDATE_TARGET"] = executable;
            helper.Environment["LAYERCAD_UPDATE_HASH"] = digest[7..];
            helper.Environment["LAYERCAD_UPDATE_FOLDER"] = folder;
            // Close uses the existing unsaved-project prompt. Cancelled closes leave the old app running.
            bool closed = false;
            void ObserveClose(object? s, EventArgs e) => closed = true;
            Closed += ObserveClose;
            using var process = Process.Start(helper) ?? throw new IOException("Could not start the update installer.");
            Close();
            Closed -= ObserveClose;
            if (closed)
                folder = null;
            else
                process.Kill();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or
                                      OperationCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            if (manual || folder is not null)
                SetStatus($"Update unavailable. {error.Message}");
        }
        finally
        {
            if (folder is not null)
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            checkingForUpdates = false;
        }
    }
}
