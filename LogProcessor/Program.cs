namespace LogProcessor
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            string filePath = @"J:\.Net task\LogAnalyzer\server_logs.txt";
            
            if (File.Exists(filePath)) { 
                Console.WriteLine("Processing file: " + filePath);
                LogAnalyzer analyzer = new LogAnalyzer();
                await analyzer.ProcessFileAsync(filePath);
            }
            else
            {
                Console.WriteLine("File not found: " + filePath);
            }

        }
    }
}
