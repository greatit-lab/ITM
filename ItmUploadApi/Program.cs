// ItmUploadApi/Program.cs

using Microsoft.Extensions.FileProviders;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

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

// ▼▼▼ [핵심] 정적 파일 제공 설정 추가 ▼▼▼
// 1. PDF 파일이 저장된 실제 물리적 경로를 지정합니다.
var fileStoragePath = "D:\\object_store";

// 2. 서버 시작 시 해당 경로가 존재하지 않으면 자동으로 생성합니다.
if (!Directory.Exists(fileStoragePath))
{
    Directory.CreateDirectory(fileStoragePath);
}

// 3. 'D:\object_store' 폴더의 파일들을 URL로 직접 접근할 수 있도록 허용합니다.
app.UseStaticFiles(new StaticFileOptions
{
    // 실제 파일 시스템 경로를 지정
    FileProvider = new PhysicalFileProvider(fileStoragePath),
    
    // 웹에서 접근할 때 사용할 URL 경로를 지정합니다.
    // ""는 http://서버주소/ 바로 뒤의 경로와 매핑하라는 의미입니다.
    // 예: http://.../SDWT/file.pdf -> D:\object_store\SDWT\file.pdf
    RequestPath = ""
});

app.UseAuthorization();

app.MapControllers();

app.Run();
