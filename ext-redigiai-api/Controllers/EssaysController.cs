using Microsoft.AspNetCore.Mvc;

namespace ext_redigiai_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class EssaysController : ControllerBase
    {
        public EssaysController() { }

        [HttpPost]
        [Route("upload")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(string))]
        public async Task<IActionResult> UploadEssayImageAsync(IFormFile image)
        {
            if (image == null || image.Length == 0)
                return BadRequest("No file uploaded.");

            var msg = string.Format("Image uploaded with sucessfully!");
            return Ok(msg);
        }
    }
}
