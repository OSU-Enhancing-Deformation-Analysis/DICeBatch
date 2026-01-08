using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DICeBatch
{
    public sealed class MainWindowViewModel : INotifyPropertyChanged
    {
        // --- Bindable properties ---
        private string _diceExePath = "";
        public string DiceExePath { get => _diceExePath; set { _diceExePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRun)); PersistSettings(); } }

        private string _refFolderA = "";
        public string RefFolderA { get => _refFolderA; set { _refFolderA = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRun)); PersistSettings(); } }

        private string _refFolderB = "";
        public string RefFolderB { get => _refFolderB; set { _refFolderB = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRun)); PersistSettings(); } }

        private string _outputFolder = "";
        public string OutputFolder { get => _outputFolder; set { _outputFolder = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRun)); PersistSettings(); } }

        private int _subsetSize = 31;
        public int SubsetSize { get => _subsetSize; set { _subsetSize = value; OnPropertyChanged(); PersistSettings(); } }

        private int _stepSize = 5;
        public int StepSize { get => _stepSize; set { _stepSize = value; OnPropertyChanged(); PersistSettings(); } }

        private int _threads = 4;
        public int Threads { get => _threads; set { _threads = value; OnPropertyChanged(); PersistSettings(); } }

        private bool _skipSelfCompare = true;
        public bool SkipSelfCompare { get => _skipSelfCompare; set { _skipSelfCompare = value; OnPropertyChanged(); PersistSettings(); } }

        private bool _isRunning;
        public bool IsRunning { get => _isRunning; private set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRun)); } }

        private double _progress;
        public double Progress { get => _progress; private set { _progress = value; OnPropertyChanged(); PersistSettings(); } }

        private string _statusText = "Idle";
        public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); PersistSettings(); } }

        private string _logText = "";
        public string LogText { get => _logText; private set { _logText = value; OnPropertyChanged(); PersistSettings(); } }

        public bool CanRun =>
            !IsRunning &&
            File.Exists(DiceExePath) &&
            Directory.Exists(RefFolderA) &&
            Directory.Exists(RefFolderB) &&
            Directory.Exists(OutputFolder);

        // --- Commands ---
        public ICommand BrowseDiceExeCommand { get; }
        public ICommand BrowseRefACommand { get; }
        public ICommand BrowseRefBCommand { get; }
        public ICommand BrowseOutputCommand { get; }
        public ICommand RunCommand { get; }
        public ICommand CancelCommand { get; }

        private CancellationTokenSource? _cts;

        private AppSettings _settings = new AppSettings();

        public MainWindowViewModel()
        {
            BrowseDiceExeCommand = new RelayCommand(_ => BrowseDiceExe());
            BrowseRefACommand = new RelayCommand(_ => BrowseFolder(path => RefFolderA = path));
            BrowseRefBCommand = new RelayCommand(_ => BrowseFolder(path => RefFolderB = path));
            BrowseOutputCommand = new RelayCommand(_ => BrowseFolder(path => OutputFolder = path));
            RunCommand = new RelayCommand(async _ => await RunBatchAsync(), _ => CanRun);
            CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);

            _settings = SettingsService.Load();

            // Apply loaded settings to your bindable properties:
            _diceExePath = _settings.DiceExePath;
            _refFolderA = _settings.RefFolderA;
            _refFolderB = _settings.RefFolderB;
            _outputFolder = _settings.OutputFolder;

            _subsetSize = _settings.SubsetSize;
            _stepSize = _settings.StepSize;
            _threads = _settings.Threads;
            _skipSelfCompare = _settings.SkipSelfCompare;


            AppendLog("Ready.");
        }

        private void PersistSettings()
        {
            _settings.DiceExePath = DiceExePath;
            _settings.RefFolderA = RefFolderA;
            _settings.RefFolderB = RefFolderB;
            _settings.OutputFolder = OutputFolder;

            _settings.SubsetSize = SubsetSize;
            _settings.StepSize = StepSize;
            _settings.Threads = Threads;
            _settings.SkipSelfCompare = SkipSelfCompare;

            SettingsService.Save(_settings);
        }

        private void BrowseDiceExe()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog()
            {
                Title = "Select DICe CLI executable",
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
                DiceExePath = dlg.FileName;
        }

        private void BrowseFolder(Action<string> setPath)
        {
            using var dlg = new FolderBrowserDialog();
            dlg.ShowNewFolderButton = true;

            if (dlg.ShowDialog() == DialogResult.OK && Directory.Exists(dlg.SelectedPath))
                setPath(dlg.SelectedPath);
        }

        private async Task RunBatchAsync()
        {
            // Basic validation (UI should prevent most cases, but keep it safe)
            if (!CanRun)
            {
                AppendLog("Cannot run: please set valid paths.");
                return;
            }

            IsRunning = true;
            Progress = 0;
            StatusText = "Building job list...";
            _cts = new CancellationTokenSource();

            try
            {
                var imagesA = EnumerateImages(RefFolderA).ToList();
                var imagesB = EnumerateImages(RefFolderB).ToList();

                if (imagesA.Count == 0 || imagesB.Count == 0)
                {
                    AppendLog("No images found in one or both reference folders.");
                    return;
                }

                // Build all pairs A x B
                var pairs = new List<(string a, string b)>(imagesA.Count * imagesB.Count);
                foreach (var a in imagesA)
                {
                    foreach (var b in imagesB)
                    {
                        if (SkipSelfCompare && string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                            continue;

                        pairs.Add((a, b));
                    }
                }

                AppendLog($"Found {imagesA.Count} images in A, {imagesB.Count} images in B.");
                AppendLog($"Total comparisons queued: {pairs.Count}");

                if (pairs.Count == 0)
                {
                    AppendLog("Nothing to run (pairs list is empty).");
                    return;
                }

                StatusText = "Running...";
                int completed = 0;

                // Run sequentially for v1 (easy + safe).
                // If you want parallel later, we can add a concurrency limiter.
                foreach (var (a, b) in pairs)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var jobName = MakeJobName(a, b);
                    var jobOutDir = Path.Combine(OutputFolder, jobName);
                    Directory.CreateDirectory(jobOutDir);

                    var templateParamsPath = GetTemplateParamsPath();
                    var jobParamsPath = Path.Combine(jobOutDir, "dice_params.xml");

                    if (!File.Exists(templateParamsPath))
                    {
                        throw new FileNotFoundException(
                            "dice_params.xml was not found next to the executable.",
                            templateParamsPath
                        );
                    }

                    File.Copy(templateParamsPath, jobParamsPath, overwrite: true);


                    var xmlPath = WriteDiceInputXml(
                        jobOutDir,
                        a,
                        b,
                        SubsetSize,
                        StepSize,
                        Threads
                    );

                    var args = BuildDiceArguments(xmlPath);

                    AppendLog($"\n=== Running: {Path.GetFileName(a)}  vs  {Path.GetFileName(b)} ===");
                    AppendLog($"Cmd: \"{DiceExePath}\" {args}");

                    var result = await RunProcessCaptureAsync(
                        exePath: DiceExePath,
                        arguments: args,
                        workingDirectory: jobOutDir,
                        token: _cts.Token
                    );

                    if (result.ExitCode != 0)
                    {
                        AppendLog($"[ERROR] ExitCode={result.ExitCode}");
                        if (!string.IsNullOrWhiteSpace(result.StdErr))
                            AppendLog(result.StdErr);
                    }
                    else
                    {
                        AppendLog("[OK]");
                    }

                    if (!string.IsNullOrWhiteSpace(result.StdOut))
                        AppendLog(result.StdOut);

                    completed++;
                    Progress = (double)completed / pairs.Count;
                    StatusText = $"Running... ({completed}/{pairs.Count})";
                }

                StatusText = "Done";
                AppendLog("\nAll jobs completed.");
            }
            catch (OperationCanceledException)
            {
                StatusText = "Canceled";
                AppendLog("\nCanceled.");
            }
            catch (Exception ex)
            {
                StatusText = "Error";
                AppendLog($"\n[EXCEPTION] {ex}");
            }
            finally
            {
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private static IEnumerable<string> EnumerateImages(string folder)
        {
            // Adjust extensions as needed
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp" };

            return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                            .Where(f => exts.Contains(Path.GetExtension(f)));
        }

        private static string GetTemplateParamsPath()
        {
            // AppContext.BaseDirectory works for Debug, Release, and published apps
            return Path.Combine(AppContext.BaseDirectory, "dice_params.xml");
        }

        private static string MakeJobName(string a, string b)
        {
            static string Sanitize(string s)
            {
                var invalid = Path.GetInvalidFileNameChars();
                var sb = new StringBuilder(s.Length);
                foreach (var ch in s)
                    sb.Append(invalid.Contains(ch) ? '_' : ch);
                return sb.ToString();
            }

            var aName = Path.GetFileNameWithoutExtension(a);
            var bName = Path.GetFileNameWithoutExtension(b);
            return Sanitize($"{aName}__vs__{bName}");
        }

        /// <summary>
        /// Create the DICe XML input file for a single comparison.
        /// </summary>
        /// <param name="outputDir"></param>
        /// <param name="refImage"></param>
        /// <param name="defImage"></param>
        /// <param name="subsetSize"></param>
        /// <param name="stepSize"></param>
        /// <param name="threads"></param>
        /// <returns></returns>
        private static string WriteDiceInputXml(
    string outputDir,
    string refImage,
    string defImage,
    int subsetSize,
    int stepSize,
    int threads // NOTE: threads usually belong in params.xml, but leaving it here doesn't hurt unless your build ignores it
)
        {
            Directory.CreateDirectory(outputDir);
            string xmlPath = Path.Combine(outputDir, "dice_input.xml");

            string imageFolder = Path.GetDirectoryName(refImage)
                ?? throw new InvalidOperationException("Could not determine reference image folder.");

            // Template comment says trailing slash/backslash is required:
            if (!outputDir.EndsWith("\\") && !outputDir.EndsWith("/"))
                outputDir += "\\";

            if (!imageFolder.EndsWith("\\") && !imageFolder.EndsWith("/"))
                imageFolder += "\\";

            string refName = Path.GetFileName(refImage);
            string defName = Path.GetFileName(defImage);

            string xml = $@"<?xml version=""1.0""?>
<ParameterList>
  <Parameter name=""output_folder"" type=""string"" value=""{EscapeXml(outputDir)}"" />
  <Parameter name=""image_folder"" type=""string"" value=""{EscapeXml(imageFolder)}"" />
  <Parameter name=""correlation_parameters_file"" type=""string"" value=""dice_params.xml"" />


  <Parameter name=""subset_size"" type=""int"" value=""{subsetSize}"" />
  <Parameter name=""step_size"" type=""int"" value=""{stepSize}"" />

  <Parameter name=""reference_image"" type=""string"" value=""{EscapeXml(refName)}"" />

  <ParameterList name=""deformed_images"">
    <Parameter name=""{EscapeXml(defName)}"" type=""bool"" value=""true"" />
  </ParameterList>
</ParameterList>";

            File.WriteAllText(xmlPath, xml);
            return xmlPath;
        }

        private static string EscapeXml(string s) =>
            s.Replace("&", "&amp;")
             .Replace("\"", "&quot;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;");



        /// <summary>
        /// Create the DICe command-line arguments for a single comparison.  DICe just takes an XML input file.
        /// </summary>
        private static string BuildDiceArguments(string xmlPath)
        {
            return $"-v -i \"{xmlPath}\"";
        }

        private static async Task<ProcessResult> RunProcessCaptureAsync(
            string exePath,
            string arguments,
            string workingDirectory,
            CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            proc.Exited += (_, __) => tcs.TrySetResult(proc.ExitCode);

            if (!proc.Start())
                throw new InvalidOperationException("Failed to start process.");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using (token.Register(() =>
            {
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
                catch { /* ignore */ }
            }))
            {
                var exitCode = await tcs.Task.ConfigureAwait(false);
                return new ProcessResult(exitCode, stdout.ToString(), stderr.ToString());
            }
        }

        private void AppendLog(string text)
        {
            LogText += (LogText.Length == 0 ? "" : "\n") + text;
        }

        // --- INotifyPropertyChanged ---
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            // Update CanExecute state for commands that depend on CanRun/IsRunning
            (RunCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
