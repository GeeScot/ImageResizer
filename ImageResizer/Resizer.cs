using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.S3.Transfer;

namespace ImageResizer
{
    public class ImageResizer
    {
        public static class ImageSettings
        {
            public const int MaxWidth = 1080;
            public const string ResizedSuffix = "optimised";
            public const int ExifOrientation = 274;
        }
        
        private readonly IAmazonS3 _client;

        public ImageResizer()
        {
            _client = new AmazonS3Client();
        }

        public ImageResizer(IAmazonS3 client)
        {
            _client = client;
        }

        public async Task<string> FunctionHandler(S3Event evnt, ILambdaContext context)
        {
            var newObjectEvent = evnt.Records?[0].S3;
            if(newObjectEvent == null)
            {
                return null;
            }

            try
            {
                var response = await _client.GetObjectAsync(newObjectEvent.Bucket.Name, newObjectEvent.Object.Key);
                
                using var responseStream = response.ResponseStream;
                using var memoryStream = new MemoryStream();
                await responseStream.CopyToAsync(memoryStream);
                
                byte[] originalImage = memoryStream.ToArray();
                byte[] resizedImage = ResizeImage(originalImage);

                var transferUtility = new TransferUtility(_client);
                await transferUtility.UploadAsync(new TransferUtilityUploadRequest
                {
                    BucketName = newObjectEvent.Bucket.Name,
                    Key = $"{newObjectEvent.Object.Key}.{ImageSettings.ResizedSuffix}.jpg",
                    InputStream = new MemoryStream(resizedImage),
                    PartSize = 10485760, // 10 MB.
                    StorageClass = S3StorageClass.Standard,
                    CannedACL = S3CannedACL.PublicRead,
                    ContentType = "image/jpeg"
                });
                
                
                return response.Headers.ContentType;
            }
            catch(Exception e)
            {
                context.Logger.LogLine($"Error getting object {newObjectEvent.Object.Key} from bucket {newObjectEvent.Bucket.Name}. Make sure they exist and your bucket is in the same region as this function.");
                context.Logger.LogLine(e.Message);
                context.Logger.LogLine(e.StackTrace);
                throw;
            }
        }
        
        private byte[] ResizeImage(byte[] imageContent)
        {
            using var originalImage = new Bitmap(new MemoryStream(imageContent));

            var orientation = originalImage.PropertyItems.Single(x => x.Id == ImageSettings.ExifOrientation);
            var orientationValue = orientation.Value[1];

            RotateFlipType rotation;
            switch (orientationValue)
            {
                case 6:
                    rotation = RotateFlipType.Rotate90FlipNone;
                    break;
                case 8:
                    rotation = RotateFlipType.Rotate270FlipNone;
                    break;
                default:
                    rotation = RotateFlipType.RotateNoneFlipNone;
                    break;
            }
            
            originalImage.RotateFlip(rotation);

            int originalWidth = originalImage.Width;
            int originalHeight = originalImage.Height;

            double aspectRatio = (double) originalHeight / originalWidth;
            int newWidth = ImageSettings.MaxWidth;
            int newHeight = (int) (newWidth * aspectRatio);

            var resized = new Bitmap(newWidth, newHeight);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.CompositingMode = CompositingMode.SourceCopy;

                graphics.DrawImage(originalImage, 0, 0, newWidth, newHeight);
            }
            
            using var memoryStream = new MemoryStream();
            resized.Save(memoryStream, ImageFormat.Jpeg);

            return memoryStream.ToArray();
        }
    }
}
