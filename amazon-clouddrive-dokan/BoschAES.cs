namespace Azi.Cloud.DokanNet
{
    using System;
    using System.IO;
    using System.Security.Cryptography;

    public class BoschAES
    {
        public ICryptoTransform Encryptor { get; set; }

        public ICryptoTransform Decryptor { get; set; }
    }
}