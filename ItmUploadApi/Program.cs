// ItmUploadApi/Program.cs
var builder = WebApplication.CreateBuyilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ▼▼▼ 바로 이 코드를 추가하세요! ▼▼▼
// 모든 IP 주소에서 8080 포트로 들어오는 요청을 받도록 설정합니다.
builder.WebHost.UseUrls("http://*:8080");

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
  app.UseSwagger();
  app.UseSwaggerUI();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
