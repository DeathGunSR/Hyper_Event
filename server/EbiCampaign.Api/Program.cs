using EbiCampaign.Api;

WebApplication app = ApiHost.Build(args);
await ApiHost.InitializeAsync(app);
await app.RunAsync();
