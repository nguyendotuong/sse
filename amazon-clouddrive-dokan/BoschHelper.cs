using System.Threading;

namespace Azi.Cloud.DokanNet
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using Tools;

    public static class BoschHelper
    {
        private const string IV = "Vector here";
        private const string KEY = "Key here";
        private static BoschAES boschAES;

        static BoschHelper()
        {
            using (AesManaged aes = new AesManaged())
            using (SHA256CryptoServiceProvider sha = new SHA256CryptoServiceProvider())
            using (MD5CryptoServiceProvider mD5 = new MD5CryptoServiceProvider())
            {
                aes.KeySize = sha.HashSize;
                aes.BlockSize = mD5.HashSize;
                aes.IV = mD5.ComputeHash(Encoding.ASCII.GetBytes(IV));
                aes.Key = sha.ComputeHash(Encoding.ASCII.GetBytes(KEY));
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.Zeros;

                boschAES = new BoschAES
                {
                    Encryptor = aes.CreateEncryptor(),
                    Decryptor = aes.CreateDecryptor()
                };
            }
        }

        public static void Encrypt(string inputFileName, string outputFileName, bool encrypt = true)
        {
            try
            {
                using (var inputFileStream = new FileStream(inputFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var outputFileStream = new FileStream(outputFileName, FileMode.OpenOrCreate, FileAccess.Write))
                using (var cryptoStream = new CryptoStream(outputFileStream, encrypt ? boschAES.Encryptor : boschAES.Decryptor, CryptoStreamMode.Write))
                {
                    int bufferSize = 4096;
                    byte[] buffer = new byte[bufferSize];
                    int bytesRead;
                    do
                    {
                        bytesRead = inputFileStream.Read(buffer, 0, bufferSize);
                        if (bytesRead != 0)
                        {
                            cryptoStream.Write(buffer, 0, bytesRead);
                        }
                    }
                    while (bytesRead != 0);
                    //cryptoStream.FlushFinalBlock();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        public static void Encrypt(string inputFileName)
        {
            try
            {
                // To copy a file to temp file
                var tempFile = inputFileName + DateTime.Now.Ticks;
                File.Copy(inputFileName, tempFile, true);

                // Decrypt temp file
                Encrypt(tempFile, inputFileName);

                // Delete temp file
                File.Delete(tempFile);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        public static void Decrypt(string inputFileName)
        {
            try
            {
                // To copy a file to temp file
                var tempFile = inputFileName + DateTime.Now.Ticks;
                File.Copy(inputFileName, tempFile, true);

                // Decrypt temp file
                Encrypt(tempFile, inputFileName, false);

                // Delete temp file
                File.Delete(tempFile);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        public static bool IsPastingFile(string fileName, FileMode mode)
        {
            return mode == FileMode.CreateNew && !string.IsNullOrEmpty(fileName)
                            && !fileName.Equals(@"\New folder");
        }
    }
}