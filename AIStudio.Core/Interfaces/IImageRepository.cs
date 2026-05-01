using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

public interface IImageRepository
{
    Task InitializeAsync();
    Task SaveImageAsync(ImageRecord image);
    Task<IReadOnlyList<ImageRecord>> LoadAllImagesAsync();
    Task DeleteImageAsync(string id);
}
