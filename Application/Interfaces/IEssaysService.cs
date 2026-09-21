using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Http;

namespace Application.Interfaces
{
    public interface IEssaysService
    {
        Task<ImageUploadResult> UploadEssayImageAsync(IFormFile image);
    }
}
