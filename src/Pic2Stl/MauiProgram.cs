using Microsoft.Extensions.Logging;
using Pic2Stl.Core.Depth;
using Pic2Stl.Services;
using Plugin.ProgressViews;

namespace Pic2Stl;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseProgressViews()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
		builder.Services.AddSingleton(services => new AiModelInstaller(
			services.GetRequiredService<HttpClient>(),
			Path.Combine(FileSystem.AppDataDirectory, "models")));
		builder.Services.AddSingleton<PhotoDecoder>();
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
