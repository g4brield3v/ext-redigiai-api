using Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ext_redigiai_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class EssaysController : ControllerBase
    {
        public readonly IEssaysService _essaysService;

        public EssaysController(IEssaysService essaysService)
        {
            _essaysService = essaysService;
        }

        [HttpPost]
        [Route("upload")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(string))]
        public async Task<IActionResult> UploadEssayImageAsync(IFormFile image)
        {
            var res = await _essaysService.UploadEssayImageAsync(image);
            return Ok(res);
        }
    }
}
