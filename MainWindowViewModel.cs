using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mime;
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

        // Property to provide all possible enum values
        public IEnumerable<PairType> PossiblePairingTypes
        {
            get
            {
                // Use Enum.GetValues to get all enum values as an array
                return (PairType[])Enum.GetValues(typeof(PairType));
            }
        }

        private PairType _pairingType = PairType.AllWithInitial;
        public PairType PairingType { get => _pairingType; set { _pairingType = value; OnPropertyChanged(); PersistSettings(); } }

        private int _jumpDist = 5;

        public int JumpDist { get => _jumpDist; set { _jumpDist = value; OnPropertyChanged(); PersistSettings(); } }

        private int _threads = 4;
        public int Threads { get => _threads; set { _threads = value; OnPropertyChanged(); PersistSettings(); } }

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
            _pairingType = _settings.PairingType;
            _jumpDist = _settings.JumpDist;

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
            _settings.PairingType = PairingType;
            _settings.JumpDist = JumpDist;

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

        private static string GetTileSuffix(string filePath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            var parts = name.Split('_');
            if (parts.Length >= 2)
            {
                // Grabs the "X_Y" off the end of "prefix_stem_X_Y"
                return $"{parts[^2]}_{parts[^1]}";
            }
            return name;
        }

        private List<(string, string)> BuildPairs(List<string> imagesA, List<string> imagesB)
        {
            return PairingType switch
            {
                PairType.Every => BuildPairsEvery(imagesA, imagesB),
                PairType.SlidingWindow => BuildPairsSequential(imagesA, imagesB, 1, slidingWindow: true),
                PairType.ContinuousChain => BuildPairsSequential(imagesA, imagesB, JumpDist, slidingWindow: false),
                PairType.AllWithInitial => BuildPairsAllRefInitial(imagesA, imagesB),
                _ => throw new InvalidOperationException("Unknown pairing type.")
            };
        }

        private List<(string, string)> BuildPairsEvery(List<string> imagesA, List<string> imagesB)
        {
            // Build all valid pairs based on spatial coordinates
            var pairs = new List<(string a, string b)>();
            foreach (var a in imagesA)
            {
                var suffixA = GetTileSuffix(a);
                foreach (var b in imagesB)
                {
                    //Don't comapre with self
                    if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var suffixB = GetTileSuffix(b);

                    // Only pair them if they came from the same tile location
                    if (suffixA == suffixB)
                    {
                        pairs.Add((a, b));
                    }
                }
            }

            return pairs;
        }

        private List<(string, string)> BuildPairsSequential(List<string> imagesA, List<string> imagesB, int jump, bool slidingWindow)
        {
            // 1. Group images by their tile coordinate (e.g., "0_0", "1_2")
            var tileGroups = imagesA.GroupBy(GetTileSuffix).ToList();
            int jumpSize = jump; // This would be the 'x' from your UI (e.g., 1 or 5)

            var pairs = new List<(string a, string b)>();

            foreach (var group in tileGroups)
            {
                // 2. Sort the images chronologically for this tile location
                var sortedTiles = group.OrderBy(f => f).ToList();

                // 3. Iterate and pair with the offset 'x'
                int offset = jumpSize; // i offset for a continuous jump (e.g., (1, 5), (5, 9), (9, 13) etc.)
                if (slidingWindow)
                    offset = 1; // i offset for a sliding window (e.g., (1, 2), (2, 3), (3, 4) etc.)

                for (int i = 0; i < sortedTiles.Count - jumpSize; i = i + offset)
                {
                    var currentFrame = sortedTiles[i];
                    var targetFrame = sortedTiles[i + jumpSize];

                    pairs.Add((currentFrame, targetFrame));
                }
            }

            return pairs;
        }

        private List<(string, string)> BuildPairsAllRefInitial(List<string> imagesA, List<string> imagesB)
        {
            // 1. Group all images by their tile coordinate (e.g., "0_0", "1_2")
            var tileGroups = imagesA.GroupBy(GetTileSuffix).ToList();

            var pairs = new List<(string a, string b)>();

            foreach (var group in tileGroups)
            {
                // 2. Sort the images in this specific tile location chronologically
                var sortedTiles = group.OrderBy(f => f).ToList();

                if (sortedTiles.Count < 2) continue;

                // 3. Set the first frame as the Reference
                var referenceFrame = sortedTiles[0];

                // 4. Compare Reference to every other frame in this location
                for (int i = 1; i < sortedTiles.Count; i++)
                {
                    pairs.Add((referenceFrame, sortedTiles[i]));
                }
            }

            return pairs;
        }

        private async Task RunBatchAsync()
        {
            if (!CanRun) return;

            IsRunning = true;
            Progress = 0;
            StatusText = "Building job list...";
            _cts = new CancellationTokenSource();

            // Track timing for the whole batch
            var batchTimer = Stopwatch.StartNew();

            try
            {
                var imagesA = EnumerateImages(RefFolderA).ToList();
                var imagesB = EnumerateImages(RefFolderB).ToList();

                var pairs = BuildPairs(imagesA, imagesB);

                if (pairs.Count == 0) return;

                int completed = 0;
                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Threads, CancellationToken = _cts.Token };

                await Parallel.ForEachAsync(pairs, parallelOptions, async (pair, token) =>
                {
                    var (a, b) = pair;
                    var jobName = MakeJobName(a, b);
                    var tempJobDir = Path.Combine(OutputFolder, $".temp_{jobName}");
                    Directory.CreateDirectory(tempJobDir);

                    // Setup and Execute DICe
                    var templateParamsPath = GetTemplateParamsPath();
                    File.Copy(templateParamsPath, Path.Combine(tempJobDir, "dice_params.xml"), true);
                    var xmlPath = WriteDiceInputXml(tempJobDir, a, b, SubsetSize, StepSize, 1);

                    await RunProcessCaptureAsync(DiceExePath, BuildDiceArguments(xmlPath), tempJobDir, token);

                    // Move result and Cleanup
                    var solutionFile = Path.Combine(tempJobDir, "DICe_solution_0.txt");
                    if (File.Exists(solutionFile))
                        File.Move(solutionFile, Path.Combine(OutputFolder, $"{jobName}.txt"), true);

                    try { Directory.Delete(tempJobDir, true); } catch { }

                    // 1. Thread-safe increment
                    int currentCompleted = Interlocked.Increment(ref completed);

                    // 2. Calculate Timing Metrics
                    long elapsedMs = batchTimer.ElapsedMilliseconds;
                    double avgMsPerJob = (double)elapsedMs / currentCompleted;
                    int remainingJobs = pairs.Count - currentCompleted;
                    TimeSpan eta = TimeSpan.FromMilliseconds(avgMsPerJob * remainingJobs);

                    // 3. Update UI
                    App.Current.Dispatcher.Invoke(() => {
                        Progress = (double)currentCompleted / pairs.Count;
                        StatusText = $"Avg: {avgMsPerJob / 1000:F2}s | ETA: {eta:hh\\:mm\\:ss} | ({currentCompleted}/{pairs.Count})";
                        AppendLog($"Finished: {jobName} ({avgMsPerJob / 1000:F2}s avg)");
                    });
                });

                StatusText = $"Done! Total Time: {batchTimer.Elapsed:hh\\:mm\\:ss}";
            }
            catch (Exception ex) { /* handle as before */ }
            finally { IsRunning = false; batchTimer.Stop(); }
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
    
    public enum PairType
    {
        Every,
        SlidingWindow,
        ContinuousChain,
        AllWithInitial
    }
}
