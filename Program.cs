using bug_reporter;
using LibVLCSharp.Shared;

namespace bug_reporter;

internal static class Program
{
	[STAThread]
	private static void Main()
	{
		Application.EnableVisualStyles();
		Application.SetDefaultFont(new Font("Segoe UI", 9f));
		_ = Task.Run(() => PrewarmMediaRuntime());
		Application.Run(new RecorderForm());
	}

	private static void PrewarmMediaRuntime()
	{
		try
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			Core.Initialize();
			string pluginPath = Path.Combine(AppContext.BaseDirectory, "plugins");
			using var libVlc = new LibVLC($"--plugin-path={pluginPath}");
			sw.Stop();
			Logger.Instance.Log($"Media runtime prewarm completed in {sw.ElapsedMilliseconds}ms.");
		}
		catch (Exception ex)
		{
			Logger.Instance.Log($"Media runtime prewarm failed: {ex.Message}");
		}
	}
}

