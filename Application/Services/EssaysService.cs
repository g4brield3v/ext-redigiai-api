using Application.Interfaces;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Application.Services
{
    public class EssaysService : IEssaysService
    {
        public readonly IConfiguration _config;
        public readonly Cloudinary _cloudinary;

        public EssaysService(IConfiguration config, Cloudinary cloudinary)
        {
            _config = config;
            _cloudinary = cloudinary;
        }

        public async Task<ImageUploadResult> UploadEssayImageAsync(IFormFile image)
        {
            using (var stream = image.OpenReadStream())
            {
                var uploadParams = new ImageUploadParams()
                {
                    File = new FileDescription(image.FileName, stream)
                };
                return await _cloudinary.UploadAsync(uploadParams);
            }
        }
    }
}
