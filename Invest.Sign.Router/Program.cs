using Invest.Sign.Router;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSigningRouter();

var host = builder.Build();
host.Run();
