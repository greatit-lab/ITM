// ItmUploadApi/Controllers/FileUploadController.cs
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace ItmUploadApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FileUploadController : ControllerBase
    {
        // 최상위 저장 경로
        private readonly string _baseStoragePath = "D:\\object_store";

        // POST /api/FileUpload/upload
        [HttpPost("upload")]
        // ★★★ IFormFile과 함께 sdwt, eqpid를 파라미터로 받도록 수정 ★★★
        public async Task<IActionResult> UploadFile([FromForm] IFormFile file, [FromForm] string sdwt, [FromForm] string eqpid)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { message = "업로드할 파일이 없습니다." });
            }
            if (string.IsNullOrEmpty(sdwt) || string.IsNullOrEmpty(eqpid))
            {
                return BadRequest(new { message = "sdwt와 eqpid 정보가 필요합니다." });
            }

            try
            {
                // 1. 전달받은 정보를 기반으로 최종 저장 경로 생성
                // 예: D:\object_store\SDWT01\EQP001\20250905
                string dateFolder = DateTime.Now.ToString("yyyyMMdd");
                string targetDirectory = Path.Combine(_baseStoragePath, sdwt, eqpid, dateFolder);

                // 2. 해당 폴더가 없으면 재귀적으로 생성
                if (!Directory.Exists(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                // 3. 고유한 파일명으로 파일 저장
                var uniqueFileName = $"{Path.GetFileNameWithoutExtension(file.FileName)}_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
                var filePath = Path.Combine(targetDirectory, uniqueFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }
                
                // 4. DB에 저장할 참조 주소 생성 (상대 경로)
                var referenceAddress = $"/{sdwt}/{eqpid}/{dateFolder}/{uniqueFileName}";

                return Ok(new { message = "파일 업로드 성공", referenceAddress = referenceAddress });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"서버 오류 발생: {ex.Message}");
            }
        }
        
        // GET /api/FileUpload/download/{fileName}
        [HttpGet("download/{fileName}")]
        public IActionResult DownloadFile(string fileName)
        {
            var filePath = Path.Combine(_baseStoragePath, fileName);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("요청한 파일을 찾을 수 없습니다.");
            }

            var memoryStream = new MemoryStream();
            using (var stream = new FileStream(filePath, FileMode.Open))
            {
                stream.CopyTo(memoryStream);
            }
            memoryStream.Position = 0;

            // 파일 종류에 맞게 Content-Type을 지정 (PDF로 가정)
            return File(memoryStream, "application/pdf", fileName);
        }

        // GET /api/FileUpload/health
        [HttpGet("health")]
        public IActionResult HealthCheck()
        {
            // ITM Agent가 서버의 정상 동작 여부를 확인할 수 있는 주소
            return Ok("ItmUploadApi is healthy.");
        }
    }
}
