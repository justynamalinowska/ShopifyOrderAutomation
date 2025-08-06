using ShopifyOrderAutomation.Configuration;
using ShopifyOrderAutomation.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddLogging();

builder.Services.Configure<InPostConfiguration>(builder.Configuration.GetSection("InPost"));
builder.Services.Configure<ShopifyConfiguration>(builder.Configuration.GetSection("Shopify"));

builder.Services.AddHttpClient<IInPostService, InPostService>();
builder.Services.AddHttpClient<IShopifyService, ShopifyService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapControllers();

app.Run();