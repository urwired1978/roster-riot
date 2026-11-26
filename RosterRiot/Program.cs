using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RosterRiot;
using RosterRiot.Services;

// Numbers in SVG attributes need "." decimals whatever language the phone uses.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<SleeperClient>();
builder.Services.AddScoped<EspnClient>();
builder.Services.AddScoped<LeagueService>();
builder.Services.AddScoped<TradeService>();
builder.Services.AddScoped<BuzzService>();

await builder.Build().RunAsync();
