using System.Windows;
using System.Windows.Controls;
using LLMMeter.Adapters;
using LLMMeter.Core;
using LLMMeter.Discovery;
using LLMMeter.Persistence;

namespace LLMMeter.UI;

public partial class AddBackendDialog : Window
{
    private readonly AppServices _services;

    public AddBackendDialog(AppServices services)
    {
        InitializeComponent();
        _services = services;
        TypeBox.ItemsSource = new[]
        {
            "Auto detect", "vLLM", "llama-server", "LM Studio", "Ollama", "OpenAI-compatible", "NInfer",
        };
        TypeBox.SelectedIndex = 0;
        UrlBox.Focus();
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            ResultText.Text = "Enter a valid base URL, e.g. http://192.168.1.31:8000";
            return;
        }

        TestButton.IsEnabled = false;
        ResultText.Text = "Probing…";
        try
        {
            string? token = string.IsNullOrWhiteSpace(TokenBox.Password) ? null : TokenBox.Password.Trim();
            using var http = HttpService.CreateOwning(HttpService.NormalizeBase(uri), TimeSpan.FromMilliseconds(800), token);

            string selectedType = TypeBox.SelectedItem as string ?? "Auto detect";
            if (selectedType == "Auto detect")
            {
                var fp = await new EndpointFingerprinter(_ => http)
                    .FingerprintAsync(HttpService.NormalizeBase(uri), CancellationToken.None);
                ResultText.Text = fp.Kind == BackendKind.Unknown
                    ? $"No recognized backend ({fp.Evidence})"
                    : $"Detected: {fp.Kind.DisplayName()} — {fp.Evidence}";
                return;
            }

            var mappedKind = BackendRegistry.ParseKind(MapType(selectedType));
            if (mappedKind == BackendKind.NInfer)
            {
                var endpoint = new EndpointRef("manual-test", HttpService.NormalizeBase(uri), OriginKind.Manual, null, token);
                var adapter = new NInferAdapter(endpoint);
                var snap = await adapter.CollectAsync(http, CancellationToken.None);
                string hostPath = NInferPathHelper.ResolveHostTelemetryPath(endpoint);
                bool fileFound = System.IO.File.Exists(hostPath);
                if (snap.State != ConnectionState.Offline)
                {
                    ResultText.Text = fileFound
                        ? $"NInfer responding ({snap.State}) · Full telemetry file found"
                        : $"NInfer responding ({snap.State}) · Limited HTTP mode";
                }
                else
                {
                    ResultText.Text = "NInfer endpoint offline or unreachable";
                }
            }
            else
            {
                IBackendAdapter adapter = mappedKind switch
                {
                    BackendKind.Vllm => new VllmAdapter(),
                    BackendKind.LlamaCpp => new LlamaCppAdapter(),
                    BackendKind.LmStudio => new LmStudioAdapter(),
                    BackendKind.Ollama => new OllamaAdapter(),
                    _ => new GenericOpenAiAdapter(),
                };
                var snap = await adapter.CollectAsync(http, CancellationToken.None);
                ResultText.Text = snap.State != ConnectionState.Offline
                    ? $"{mappedKind.DisplayName()} responding ({snap.State}) · {(snap.ModelName ?? "catalog available")}"
                    : $"{mappedKind.DisplayName()} endpoint offline or unreachable";
            }
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _) ||
            !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            ResultText.Text = "Enter a valid base URL, e.g. http://192.168.1.31:8000";
            return;
        }

        AddButton.IsEnabled = false;
        try
        {
            var manual = new ManualEndpointConfig
            {
                Name = NameBox.Text.Trim(),
                Url = url,
                Type = MapType(TypeBox.SelectedItem as string ?? "Auto detect"),
            };
            if (!string.IsNullOrWhiteSpace(TokenBox.Password))
            {
                if (!CredentialProtection.TryProtect(TokenBox.Password.Trim(), out var protectedKey))
                {
                    ResultText.Text = "Could not securely store API token";
                    AddButton.IsEnabled = true;
                    return;
                }
                manual.ApiKey = protectedKey;
            }
            await _services.Registry.AddManualEndpointAsync(manual);

            // bind the newest window (or create one) to it right away
            var entries = _services.Registry.GetTargetEntries();
            var entry = entries.LastOrDefault(x => x.GroupLabel == "Manual");
            if (entry != null)
            {
                var win = _services.Widgets.Windows.LastOrDefault(w => !w.IsVisible)
                          ?? _services.Widgets.CreateWindow(null);
                win.Show();
                win.Bind(entry);
            }
            Close();
        }
        catch (ArgumentException ex)
        {
            ResultText.Text = ex.Message;
            AddButton.IsEnabled = true;
        }
    }

    internal static string MapType(string uiName) => uiName switch
    {
        "vLLM" => "Vllm",
        "llama-server" => "LlamaCpp",
        "LM Studio" => "LmStudio",
        "Ollama" => "Ollama",
        "OpenAI-compatible" => "OpenAi",
        "NInfer" => "NInfer",
        _ => "Auto",
    };


    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
