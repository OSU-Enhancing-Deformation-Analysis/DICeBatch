namespace DICeBatch
{
    public sealed class AppSettings
    {
        public string DiceExePath { get; set; } = "";
        public string RefFolderA { get; set; } = "";
        public string RefFolderB { get; set; } = "";
        public string OutputFolder { get; set; } = "";

        public int SubsetSize { get; set; } = 31;
        public int StepSize { get; set; } = 5;
        public int Threads { get; set; } = 4;

        public bool SkipSelfCompare { get; set; } = true;
    }
}