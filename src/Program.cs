using ShopifyOrderAutomation.Configuration;
using ShopifyOrderAutomation.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<InPostConfiguration>(builder.Configuration.GetSection("InPostConfiguration"));
builder.Services.Configure<ShopifyConfiguration>(builder.Configuration.GetSection("ShopifyConfiguration"));

builder.Services.AddHttpClient<IInPostService, InPostService>();
builder.Services.AddHttpClient<IShopifyService, ShopifyService>();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.Run();