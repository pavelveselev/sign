using Invest.Sign.Router;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSigningRouter();

var app = builder.Build();
app.MapDocumentStatusEndpoints();
app.Run();
