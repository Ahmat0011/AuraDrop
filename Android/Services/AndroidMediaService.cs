using System.Collections.ObjectModel;
using AuraDrop.AndroidApp.Models;
using Microsoft.Maui.Controls;

#pragma warning disable CA1422
#pragma warning disable CA1416
#pragma warning disable CS8604

#if ANDROID
using Android.Content;
using Android.Provider;
using Android.Database;
using Android.Graphics;
using Android.Net;
using Android.OS;
using Android.Content.PM;
#endif

namespace AuraDrop.AndroidApp.Services;

public class AndroidMediaService
{
    private static AndroidMediaService? _instance;
    public static AndroidMediaService Instance => _instance ??= new AndroidMediaService();

    private readonly List<MediaGalleryItem> _allMediaItems = new();
    private readonly Dictionary<string, MediaCategory> _categories = new();

    public async Task<bool> RequestPermissionsAsync()
    {
#if ANDROID
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity == null) return false;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                // Android 13+ (API 33+)
                var permissions = new List<string>
                {
                    Android.Manifest.Permission.ReadMediaImages,
                    Android.Manifest.Permission.ReadMediaVideo
                };

                // Android 14+ (API 34+) partial access permission
                if ((int)Build.VERSION.SdkInt >= 34)
                {
                    permissions.Add("android.permission.READ_MEDIA_VISUAL_USER_SELECTED");
                }

                var needed = permissions.Where(p => activity.CheckSelfPermission(p) != Permission.Granted).ToList();
                if (needed.Count > 0)
                {
                    activity.RequestPermissions(needed.ToArray(), 1002);
                    // Give the OS a moment to process the user prompt
                    await Task.Delay(500);
                }
            }
            else
            {
                // Android <= 12
                if (activity.CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage) != Permission.Granted)
                {
                    activity.RequestPermissions(new[] { Android.Manifest.Permission.ReadExternalStorage }, 1001);
                    await Task.Delay(500);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Permission request exception: {ex.Message}");
            return true;
        }
#else
        await Task.CompletedTask;
        return true;
#endif
    }

    public async Task<(List<MediaGalleryItem> Items, List<MediaCategory> Categories)> LoadAllMediaAsync()
    {
        return await Task.Run(() =>
        {
            _allMediaItems.Clear();
            _categories.Clear();

#if ANDROID
            var context = Android.App.Application.Context;
            var resolver = context?.ContentResolver;
            if (resolver == null)
            {
                return (_allMediaItems, new List<MediaCategory>());
            }

            // 1. Fetch Images
            try
            {
                var imageUri = MediaStore.Images.Media.ExternalContentUri;
                string[] imageProjection = {
                    MediaStore.MediaColumns.Id,
                    MediaStore.MediaColumns.DisplayName,
                    MediaStore.MediaColumns.Data,
                    MediaStore.MediaColumns.Size,
                    MediaStore.MediaColumns.MimeType,
                    MediaStore.MediaColumns.DateAdded,
                    MediaStore.MediaColumns.BucketDisplayName
                };

                using var imageCursor = resolver.Query(
                    imageUri,
                    imageProjection,
                    null,
                    null,
                    MediaStore.MediaColumns.DateAdded + " DESC"
                );

                if (imageCursor != null)
                {
                    int idCol = imageCursor.GetColumnIndex(MediaStore.MediaColumns.Id);
                    int nameCol = imageCursor.GetColumnIndex(MediaStore.MediaColumns.DisplayName);
                    int dataCol = imageCursor.GetColumnIndex(MediaStore.MediaColumns.Data);
                    int sizeCol = imageCursor.GetColumnIndex(MediaStore.MediaColumns.Size);
                    int mimeCol = imageCursor.GetColumnIndex(MediaStore.MediaColumns.MimeType);
                    int dateCol = imageCursor.GetColumnIndex(MediaStore.MediaColumns.DateAdded);
                    int bucketCol = imageCursor.GetColumnIndex(MediaStore.MediaColumns.BucketDisplayName);

                    while (imageCursor.MoveToNext())
                    {
                        try
                        {
                            long id = idCol >= 0 ? imageCursor.GetLong(idCol) : 0;
                            string name = nameCol >= 0 ? imageCursor.GetString(nameCol) ?? "Bild" : "Bild";
                            string path = dataCol >= 0 ? imageCursor.GetString(dataCol) ?? "" : "";
                            long size = sizeCol >= 0 ? imageCursor.GetLong(sizeCol) : 0;
                            string mime = mimeCol >= 0 ? imageCursor.GetString(mimeCol) ?? "image/jpeg" : "image/jpeg";
                            long dateAdded = dateCol >= 0 ? imageCursor.GetLong(dateCol) : 0;
                            string bucket = bucketCol >= 0 ? imageCursor.GetString(bucketCol) ?? "Bilder" : "Bilder";

                            var contentUri = ContentUris.WithAppendedId(imageUri, id);
                            var thumbSource = CreateSafeImageSource(resolver, contentUri, path);

                            _allMediaItems.Add(new MediaGalleryItem
                            {
                                Id = id,
                                UriString = contentUri.ToString(),
                                FilePath = path,
                                DisplayName = name,
                                FileSize = size,
                                MimeType = mime,
                                IsVideo = false,
                                BucketDisplayName = bucket,
                                DateAdded = dateAdded,
                                ThumbnailSource = thumbSource
                            });
                        }
                        catch (Exception itemEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error reading image item: {itemEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error querying images from MediaStore: {ex.Message}");
            }

            // 2. Fetch Videos
            try
            {
                var videoUri = MediaStore.Video.Media.ExternalContentUri;
                string[] videoProjection = {
                    MediaStore.MediaColumns.Id,
                    MediaStore.MediaColumns.DisplayName,
                    MediaStore.MediaColumns.Data,
                    MediaStore.MediaColumns.Size,
                    MediaStore.MediaColumns.MimeType,
                    MediaStore.MediaColumns.DateAdded,
                    MediaStore.MediaColumns.BucketDisplayName,
                    MediaStore.Video.VideoColumns.Duration
                };

                using var videoCursor = resolver.Query(
                    videoUri,
                    videoProjection,
                    null,
                    null,
                    MediaStore.MediaColumns.DateAdded + " DESC"
                );

                if (videoCursor != null)
                {
                    int idCol = videoCursor.GetColumnIndex(MediaStore.MediaColumns.Id);
                    int nameCol = videoCursor.GetColumnIndex(MediaStore.MediaColumns.DisplayName);
                    int dataCol = videoCursor.GetColumnIndex(MediaStore.MediaColumns.Data);
                    int sizeCol = videoCursor.GetColumnIndex(MediaStore.MediaColumns.Size);
                    int mimeCol = videoCursor.GetColumnIndex(MediaStore.MediaColumns.MimeType);
                    int dateCol = videoCursor.GetColumnIndex(MediaStore.MediaColumns.DateAdded);
                    int bucketCol = videoCursor.GetColumnIndex(MediaStore.MediaColumns.BucketDisplayName);
                    int durationCol = videoCursor.GetColumnIndex(MediaStore.Video.VideoColumns.Duration);

                    while (videoCursor.MoveToNext())
                    {
                        try
                        {
                            long id = idCol >= 0 ? videoCursor.GetLong(idCol) : 0;
                            string name = nameCol >= 0 ? videoCursor.GetString(nameCol) ?? "Video" : "Video";
                            string path = dataCol >= 0 ? videoCursor.GetString(dataCol) ?? "" : "";
                            long size = sizeCol >= 0 ? videoCursor.GetLong(sizeCol) : 0;
                            string mime = mimeCol >= 0 ? videoCursor.GetString(mimeCol) ?? "video/mp4" : "video/mp4";
                            long dateAdded = dateCol >= 0 ? videoCursor.GetLong(dateCol) : 0;
                            string bucket = bucketCol >= 0 ? videoCursor.GetString(bucketCol) ?? "Videos" : "Videos";
                            long duration = durationCol >= 0 ? videoCursor.GetLong(durationCol) : 0;

                            var contentUri = ContentUris.WithAppendedId(videoUri, id);
                            var thumbSource = CreateSafeImageSource(resolver, contentUri, path);

                            _allMediaItems.Add(new MediaGalleryItem
                            {
                                Id = id,
                                UriString = contentUri.ToString(),
                                FilePath = path,
                                DisplayName = name,
                                FileSize = size,
                                MimeType = mime,
                                IsVideo = true,
                                DurationMs = duration,
                                BucketDisplayName = bucket,
                                DateAdded = dateAdded,
                                ThumbnailSource = thumbSource
                            });
                        }
                        catch (Exception itemEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error reading video item: {itemEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error querying videos from MediaStore: {ex.Message}");
            }
#endif

            // Sort all media items newest first
            _allMediaItems.Sort((a, b) => b.DateAdded.CompareTo(a.DateAdded));

            // Build Categories
            var categoryList = new List<MediaCategory>();

            // 1. Alle Medien (Bilder + Videos)
            var recentCat = new MediaCategory
            {
                Key = "all",
                Title = "Alle Medien",
                Count = _allMediaItems.Count,
                CoverImage = _allMediaItems.FirstOrDefault()?.ThumbnailSource,
                IsSelected = true
            };
            categoryList.Add(recentCat);

            // 2. Bilder
            var imageItems = _allMediaItems.Where(m => !m.IsVideo).ToList();
            categoryList.Add(new MediaCategory
            {
                Key = "images",
                Title = "Bilder",
                Count = imageItems.Count,
                CoverImage = imageItems.FirstOrDefault()?.ThumbnailSource
            });

            // 3. Videos
            var videoItems = _allMediaItems.Where(m => m.IsVideo).ToList();
            categoryList.Add(new MediaCategory
            {
                Key = "videos",
                Title = "Videos",
                Count = videoItems.Count,
                CoverImage = videoItems.FirstOrDefault()?.ThumbnailSource
            });

            // 4. Screenshots
            var screenshotItems = _allMediaItems.Where(m => 
                (!string.IsNullOrEmpty(m.BucketDisplayName) && m.BucketDisplayName.Contains("Screenshot", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(m.FilePath) && m.FilePath.Contains("Screenshot", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(m.DisplayName) && m.DisplayName.Contains("Screenshot", StringComparison.OrdinalIgnoreCase))
            ).ToList();
            categoryList.Add(new MediaCategory
            {
                Key = "screenshots",
                Title = "Screenshots",
                Count = screenshotItems.Count,
                CoverImage = screenshotItems.FirstOrDefault()?.ThumbnailSource
            });

            // 5. Kamera (DCIM / Camera)
            var cameraItems = _allMediaItems.Where(m => 
                (!string.IsNullOrEmpty(m.BucketDisplayName) && m.BucketDisplayName.Equals("Camera", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(m.FilePath) && m.FilePath.Contains("DCIM", StringComparison.OrdinalIgnoreCase))
            ).ToList();
            if (cameraItems.Count > 0)
            {
                categoryList.Add(new MediaCategory
                {
                    Key = "camera",
                    Title = "Kamera",
                    Count = cameraItems.Count,
                    CoverImage = cameraItems.FirstOrDefault()?.ThumbnailSource
                });
            }

            // 6. Dynamisch gefundene Benutzer-Alben (WhatsApp, Downloads, etc.)
            var otherBuckets = _allMediaItems
                .Where(m => !string.IsNullOrEmpty(m.BucketDisplayName) && 
                            !m.BucketDisplayName.Equals("Camera", StringComparison.OrdinalIgnoreCase) && 
                            !m.BucketDisplayName.Contains("Screenshot", StringComparison.OrdinalIgnoreCase) &&
                            !m.BucketDisplayName.Equals("Bilder", StringComparison.OrdinalIgnoreCase) &&
                            !m.BucketDisplayName.Equals("Videos", StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => m.BucketDisplayName)
                .OrderByDescending(g => g.Count());

            foreach (var group in otherBuckets)
            {
                categoryList.Add(new MediaCategory
                {
                    Key = $"bucket_{group.Key}",
                    Title = group.Key,
                    Count = group.Count(),
                    CoverImage = group.FirstOrDefault()?.ThumbnailSource
                });
            }

            return (_allMediaItems, categoryList);
        });
    }

#if ANDROID
    private static ImageSource? CreateSafeImageSource(ContentResolver resolver, Android.Net.Uri contentUri, string filePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                return ImageSource.FromFile(filePath);
            }

            var uriCopy = contentUri;
            return ImageSource.FromStream(() =>
            {
                try
                {
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                    {
                        var bmp = resolver.LoadThumbnail(uriCopy, new Android.Util.Size(180, 180), null);
                        if (bmp != null)
                        {
                            var ms = new MemoryStream();
                            bmp.Compress(Bitmap.CompressFormat.Jpeg, 80, ms);
                            ms.Position = 0;
                            return ms;
                        }
                    }
                    return resolver.OpenInputStream(uriCopy) ?? Stream.Null;
                }
                catch
                {
                    return Stream.Null;
                }
            });
        }
        catch
        {
            return null;
        }
    }
#endif

    public List<MediaGalleryItem> FilterByCategory(string categoryKey)
    {
        return categoryKey switch
        {
            "all" => _allMediaItems,
            "images" => _allMediaItems.Where(m => !m.IsVideo).ToList(),
            "videos" => _allMediaItems.Where(m => m.IsVideo).ToList(),
            "screenshots" => _allMediaItems.Where(m => 
                (!string.IsNullOrEmpty(m.BucketDisplayName) && m.BucketDisplayName.Contains("Screenshot", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(m.FilePath) && m.FilePath.Contains("Screenshot", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(m.DisplayName) && m.DisplayName.Contains("Screenshot", StringComparison.OrdinalIgnoreCase))
            ).ToList(),
            "camera" => _allMediaItems.Where(m => 
                (!string.IsNullOrEmpty(m.BucketDisplayName) && m.BucketDisplayName.Equals("Camera", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(m.FilePath) && m.FilePath.Contains("DCIM", StringComparison.OrdinalIgnoreCase))
            ).ToList(),
            _ => categoryKey.StartsWith("bucket_") 
                ? _allMediaItems.Where(m => m.BucketDisplayName == categoryKey.Substring(7)).ToList()
                : _allMediaItems
        };
    }
}
