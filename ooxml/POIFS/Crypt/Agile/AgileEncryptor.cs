/* ====================================================================
   Licensed to the Apache Software Foundation (ASF) under one or more
   contributor license agreements.  See the NOTICE file distributed with
   this work for Additional information regarding copyright ownership.
   The ASF licenses this file to You under the Apache License, Version 2.0
   (the "License"); you may not use this file except in compliance with
   the License.  You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
==================================================================== */
namespace NPOI.POIFS.Crypt.Agile
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using NPOI.OpenXmlFormats.Encryption;
    using NPOI.POIFS.Crypt;
    using NPOI.POIFS.Crypt.Standard;
    using NPOI.POIFS.FileSystem;
    using NPOI.Util;
    using static NPOI.POIFS.Crypt.Agile.AgileEncryptionVerifier;
    using static NPOI.POIFS.Crypt.CryptoFunctions;

    public class AgileEncryptor : Encryptor {
        private AgileEncryptionInfoBuilder builder;
        private byte[] integritySalt;
        private byte[] pwHash;

        protected internal AgileEncryptor(AgileEncryptionInfoBuilder builder) {
            this.builder = builder;
        }

        public override void ConfirmPassword(String password) {
            // see [MS-OFFCRYPTO] - 2.3.3 EncryptionVerifier
            Random r = new Random();
            int blockSize = builder.GetHeader().BlockSize;
            int keySize = builder.GetHeader().KeySize / 8;
            int hashSize = builder.GetHeader().HashAlgorithm.hashSize;

            byte[] verifierSalt = new byte[blockSize]
                 , verifier = new byte[blockSize]
                 , keySalt = new byte[blockSize]
                 , keySpec = new byte[keySize]
                 , integritySalt = new byte[hashSize];
            r.NextBytes(verifierSalt); // blocksize
            r.NextBytes(verifier); // blocksize
            r.NextBytes(keySalt); // blocksize
            r.NextBytes(keySpec); // keysize
            r.NextBytes(integritySalt); // hashsize

            ConfirmPassword(password, keySpec, keySalt, verifierSalt, verifier, integritySalt);
        }

        public override void ConfirmPassword(String password, byte[] keySpec, byte[] keySalt, byte[] verifier, byte[] verifierSalt, byte[] integritySalt) {
            AgileEncryptionVerifier ver = builder.GetVerifier();
            ver.Salt = (/*setter*/verifierSalt);
            AgileEncryptionHeader header = builder.GetHeader();
            header.KeySalt = (/*setter*/keySalt);
            HashAlgorithm hashAlgo = ver.HashAlgorithm;

            int blockSize = header.BlockSize;

            pwHash = HashPassword(password, hashAlgo, verifierSalt, ver.SpinCount);

            /**
             * encryptedVerifierHashInput: This attribute MUST be generated by using the following steps:
             * 1. Generate a random array of bytes with the number of bytes used specified by the saltSize
             *    attribute.
             * 2. Generate an encryption key as specified in section 2.3.4.11 by using the user-supplied password,
             *    the binary byte array used to create the saltValue attribute, and a blockKey byte array
             *    consisting of the following bytes: 0xfe, 0xa7, 0xd2, 0x76, 0x3b, 0x4b, 0x9e, and 0x79.
             * 3. Encrypt the random array of bytes generated in step 1 by using the binary form of the saltValue
             *    attribute as an Initialization vector as specified in section 2.3.4.12. If the array of bytes is not an
             *    integral multiple of blockSize bytes, pad the array with 0x00 to the next integral multiple of
             *    blockSize bytes.
             * 4. Use base64 to encode the result of step 3.
             */
            byte[] encryptedVerifier = AgileDecryptor.hashInput(builder, pwHash, AgileDecryptor.kVerifierInputBlock, verifier, Cipher.ENCRYPT_MODE);
            ver.EncryptedVerifier = (/*setter*/encryptedVerifier);


            /**
             * encryptedVerifierHashValue: This attribute MUST be generated by using the following steps:
             * 1. Obtain the hash value of the random array of bytes generated in step 1 of the steps for
             *    encryptedVerifierHashInput.
             * 2. Generate an encryption key as specified in section 2.3.4.11 by using the user-supplied password,
             *    the binary byte array used to create the saltValue attribute, and a blockKey byte array
             *    consisting of the following bytes: 0xd7, 0xaa, 0x0f, 0x6d, 0x30, 0x61, 0x34, and 0x4e.
             * 3. Encrypt the hash value obtained in step 1 by using the binary form of the saltValue attribute as
             *    an Initialization vector as specified in section 2.3.4.12. If hashSize is not an integral multiple of
             *    blockSize bytes, pad the hash value with 0x00 to an integral multiple of blockSize bytes.
             * 4. Use base64 to encode the result of step 3.
             */
            MessageDigest hashMD = GetMessageDigest(hashAlgo);
            byte[] hashedVerifier = hashMD.Digest(verifier);
            byte[] encryptedVerifierHash = AgileDecryptor.hashInput(builder, pwHash, AgileDecryptor.kHashedVerifierBlock, hashedVerifier, Cipher.ENCRYPT_MODE);
            ver.EncryptedVerifierHash = (/*setter*/encryptedVerifierHash);

            /**
             * encryptedKeyValue: This attribute MUST be generated by using the following steps:
             * 1. Generate a random array of bytes that is the same size as specified by the
             *    Encryptor.KeyData.keyBits attribute of the parent element.
             * 2. Generate an encryption key as specified in section 2.3.4.11, using the user-supplied password,
             *    the binary byte array used to create the saltValue attribute, and a blockKey byte array
             *    consisting of the following bytes: 0x14, 0x6e, 0x0b, 0xe7, 0xab, 0xac, 0xd0, and 0xd6.
             * 3. Encrypt the random array of bytes generated in step 1 by using the binary form of the saltValue
             *    attribute as an Initialization vector as specified in section 2.3.4.12. If the array of bytes is not an
             *    integral multiple of blockSize bytes, pad the array with 0x00 to an integral multiple of
             *    blockSize bytes.
             * 4. Use base64 to encode the result of step 3.
             */
            byte[] encryptedKey = AgileDecryptor.hashInput(builder, pwHash, AgileDecryptor.kCryptoKeyBlock, keySpec, Cipher.ENCRYPT_MODE);
            ver.EncryptedKey = (/*setter*/encryptedKey);

            ISecretKey secretKey = new SecretKeySpec(keySpec, ver.CipherAlgorithm.jceId);
            SetSecretKey(secretKey);

            /*
             * 2.3.4.14 DataIntegrity Generation (Agile Encryption)
             * 
             * The DataIntegrity element Contained within an Encryption element MUST be generated by using
             * the following steps:
             * 1. Obtain the intermediate key by decrypting the encryptedKeyValue from a KeyEncryptor
             *    Contained within the KeyEncryptors sequence. Use this key for encryption operations in the
             *    remaining steps of this section.
             * 2. Generate a random array of bytes, known as Salt, of the same length as the value of the
             *    KeyData.HashSize attribute.
             * 3. Encrypt the random array of bytes generated in step 2 by using the binary form of the
             *    KeyData.saltValue attribute and a blockKey byte array consisting of the following bytes:
             *    0x5f, 0xb2, 0xad, 0x01, 0x0c, 0xb9, 0xe1, and 0xf6 used to form an Initialization vector as
             *    specified in section 2.3.4.12. If the array of bytes is not an integral multiple of blockSize
             *    bytes, pad the array with 0x00 to the next integral multiple of blockSize bytes.
             * 4. Assign the encryptedHmacKey attribute to the base64-encoded form of the result of step 3.
             * 5. Generate an HMAC, as specified in [RFC2104], of the encrypted form of the data (message),
             *    which the DataIntegrity element will verify by using the Salt generated in step 2 as the key.
             *    Note that the entire EncryptedPackage stream (1), including the StreamSize field, MUST be
             *    used as the message.
             * 6. Encrypt the HMAC as in step 3 by using a blockKey byte array consisting of the following bytes:
             *    0xa0, 0x67, 0x7f, 0x02, 0xb2, 0x2c, 0x84, and 0x33.
             * 7.  Assign the encryptedHmacValue attribute to the base64-encoded form of the result of step 6. 
             */
            this.integritySalt = integritySalt;

            try {
                byte[] vec = CryptoFunctions.GenerateIv(hashAlgo, header.KeySalt, AgileDecryptor.kIntegrityKeyBlock, header.BlockSize);
                Cipher cipher = GetCipher(secretKey, ver.CipherAlgorithm, ver.ChainingMode, vec, Cipher.ENCRYPT_MODE);
                byte[] FilledSalt = GetBlock0(integritySalt, AgileDecryptor.GetNextBlockSize(integritySalt.Length, blockSize));
                byte[] encryptedHmacKey = cipher.DoFinal(FilledSalt);
                header.SetEncryptedHmacKey(encryptedHmacKey);

                cipher = Cipher.GetInstance("RSA");
                foreach (AgileCertificateEntry ace in ver.GetCertificates()) {
                    cipher.Init(Cipher.ENCRYPT_MODE, ace.x509.GetPublicKey());
                    ace.encryptedKey = cipher.DoFinal(GetSecretKey().GetEncoded());
                    Mac x509Hmac = CryptoFunctions.GetMac(hashAlgo);
                    x509Hmac.Init(GetSecretKey());
                    ace.certVerifier = x509Hmac.DoFinal(ace.x509.GetEncoded());
                }
            } catch (Exception e) {
                throw new EncryptedDocumentException(e);
            }
        }

        public override Stream GetDataStream(DirectoryNode dir)
        {
            // TODO: Initialize headers
            AgileCipherOutputStream countStream = new AgileCipherOutputStream(dir, builder, GetSecretKey(), this);
            return countStream.GetStream();
        }

        /**
         * Generate an HMAC, as specified in [RFC2104], of the encrypted form of the data (message), 
         * which the DataIntegrity element will verify by using the Salt generated in step 2 as the key. 
         * Note that the entire EncryptedPackage stream (1), including the StreamSize field, MUST be 
         * used as the message.
         * 
         * Encrypt the HMAC as in step 3 by using a blockKey byte array consisting of the following bytes:
         * 0xa0, 0x67, 0x7f, 0x02, 0xb2, 0x2c, 0x84, and 0x33.
         **/
        protected void UpdateIntegrityHMAC(FileInfo tmpFile, int oleStreamSize) {
            // as the integrity hmac needs to contain the StreamSize,
            // it's not possible to calculate it on-the-fly while buffering
            // TODO: add stream size parameter to GetDataStream()
            AgileEncryptionVerifier ver = builder.GetVerifier();
            HashAlgorithm hashAlgo = ver.HashAlgorithm;
            Mac integrityMD = CryptoFunctions.GetMac(hashAlgo);
            integrityMD.Init(new SecretKeySpec(integritySalt, hashAlgo.jceHmacId));

            byte[] buf = new byte[1024];
            LittleEndian.PutLong(buf, 0, oleStreamSize);
            integrityMD.Update(buf, 0, LittleEndian.LONG_SIZE);

            FileStream fis = tmpFile.Create();
            try {
                int readBytes;
                while ((readBytes = fis.Read(buf, 0, buf.Length)) > 0) {
                    integrityMD.Update(buf, 0, readBytes);
                }
            } finally {
                fis.Close();
            }

            byte[] hmacValue = integrityMD.DoFinal();

            AgileEncryptionHeader header = builder.GetHeader();
            int blockSize = header.BlockSize;
            byte[] iv = CryptoFunctions.GenerateIv(header.HashAlgorithm, header.KeySalt, AgileDecryptor.kIntegrityValueBlock, blockSize);
            Cipher cipher = CryptoFunctions.GetCipher(GetSecretKey(), header.CipherAlgorithm, header.ChainingMode, iv, Cipher.ENCRYPT_MODE);
            byte[] hmacValueFilled = GetBlock0(hmacValue, AgileDecryptor.GetNextBlockSize(hmacValue.Length, blockSize));
            byte[] encryptedHmacValue = cipher.DoFinal(hmacValueFilled);

            header.SetEncryptedHmacValue(encryptedHmacValue);
        }

        private CT_KeyEncryptorUri passwordUri = CT_KeyEncryptorUri.httpschemasmicrosoftcomoffice2006keyEncryptorpassword;
        private CT_KeyEncryptorUri certificateUri = CT_KeyEncryptorUri.httpschemasmicrosoftcomoffice2006keyEncryptorcertificate;

        protected EncryptionDocument CreateEncryptionDocument() {
            AgileEncryptionVerifier ver = builder.GetVerifier();
            AgileEncryptionHeader header = builder.GetHeader();

            EncryptionDocument ed = EncryptionDocument.NewInstance();
            CT_Encryption edRoot = ed.AddNewEncryption();

            CT_KeyData keyData = edRoot.AddNewKeyData();
            CT_KeyEncryptors keyEncList = edRoot.AddNewKeyEncryptors();
            CT_KeyEncryptor keyEnc = keyEncList.AddNewKeyEncryptor();
            keyEnc.uri = (/*setter*/passwordUri);
            CT_PasswordKeyEncryptor keyPass = keyEnc.AddNewEncryptedPasswordKey();

            keyPass.spinCount = (uint)ver.SpinCount;

            keyData.saltSize = (uint)header.BlockSize;
            keyPass.saltSize = (uint)header.BlockSize;

            keyData.blockSize = (uint)header.BlockSize;
            keyPass.blockSize = (uint)header.BlockSize;

            keyData.keyBits = (uint)header.KeySize;
            keyPass.keyBits = (uint)header.KeySize;

            HashAlgorithm hashAlgo = header.HashAlgorithm;
            keyData.hashSize = (uint)hashAlgo.hashSize;
            keyPass.hashSize = (uint)hashAlgo.hashSize;

            ST_CipherAlgorithm? xmlCipherAlgo = (ST_CipherAlgorithm?)Enum.Parse(typeof(ST_CipherAlgorithm),header.CipherAlgorithm.xmlId);
            if (xmlCipherAlgo == null) {
                throw new EncryptedDocumentException("CipherAlgorithm " + header.CipherAlgorithm + " not supported.");
            }
            keyData.cipherAlgorithm = (/*setter*/xmlCipherAlgo.Value);
            keyPass.cipherAlgorithm = (/*setter*/xmlCipherAlgo.Value);

            switch (header.ChainingMode.jceId) {
                case "cbc":
                    keyData.cipherChaining = (/*setter*/ST_CipherChaining.ChainingModeCBC);
                    keyPass.cipherChaining = (/*setter*/ST_CipherChaining.ChainingModeCBC);
                    break;
                case "cfb":
                    keyData.cipherChaining = (/*setter*/ST_CipherChaining.ChainingModeCFB);
                    keyPass.cipherChaining = (/*setter*/ST_CipherChaining.ChainingModeCFB);
                    break;
                default:
                    throw new EncryptedDocumentException("ChainingMode " + header.ChainingMode + " not supported.");
            }

            ST_HashAlgorithm? xmlHashAlgo = (ST_HashAlgorithm?)Enum.Parse(typeof(ST_HashAlgorithm), hashAlgo.ecmaString);
            if (xmlHashAlgo == null) {
                throw new EncryptedDocumentException("HashAlgorithm " + hashAlgo + " not supported.");
            }
            keyData.hashAlgorithm = (/*setter*/xmlHashAlgo.Value);
            keyPass.hashAlgorithm = (/*setter*/xmlHashAlgo.Value);

            keyData.saltValue = (/*setter*/header.KeySalt);
            keyPass.saltValue = (/*setter*/ver.Salt);
            keyPass.encryptedVerifierHashInput = (/*setter*/ver.EncryptedVerifier);
            keyPass.encryptedVerifierHashValue = (/*setter*/ver.EncryptedVerifierHash);
            keyPass.encryptedKeyValue = (/*setter*/ver.EncryptedKey);

            CT_DataIntegrity hmacData = edRoot.AddNewDataIntegrity();
            hmacData.encryptedHmacKey = (/*setter*/header.GetEncryptedHmacKey());
            hmacData.encryptedHmacValue = (/*setter*/header.GetEncryptedHmacValue());

            foreach (AgileCertificateEntry ace in ver.GetCertificates()) {
                keyEnc = keyEncList.AddNewKeyEncryptor();
                keyEnc.uri = (/*setter*/certificateUri);
                CT_CertificateKeyEncryptor certData = keyEnc.AddNewEncryptedCertificateKey();
                try {
                    certData.X509Certificate = ace.x509.GetEncoded();
                } catch (Exception e) {
                    throw new EncryptedDocumentException(e);
                }
                certData.encryptedKeyValue = (/*setter*/ace.encryptedKey);
                certData.certVerifier = (/*setter*/ace.certVerifier);
            }

            return ed;
        }

        protected void marshallEncryptionDocument(EncryptionDocument ed, LittleEndianByteArrayOutputStream os) {
            //XmlOptions xo = new XmlOptions();
            //xo.SetCharacterEncoding("UTF-8");
            Dictionary<String, String> nsMap = new Dictionary<String, String>();
            nsMap.Add(passwordUri.ToString(), "p");
            nsMap.Add(certificateUri.ToString(), "c");
            //xo.UseDefaultNamespace();
            //xo.SaveSuggestedPrefixes(nsMap);
            //xo.SaveNamespacesFirst();
            //xo.SaveAggressiveNamespaces();

            // Setting standalone doesn't work with xmlbeans-2.3 & 2.6
            // ed.DocumentProperties().Standalone=(/*setter*/true);
            //xo.SaveNoXmlDecl();
            MemoryStream bos = new MemoryStream();
            try {
                byte[] buf = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
                bos.Write(buf, 0, buf.Length);
                ed.Save(bos);
                os.Write(bos.ToArray());
            } catch (IOException e) {
                throw new EncryptedDocumentException("error marshalling encryption info document", e);
            }
        }

        protected void CreateEncryptionInfoEntry(DirectoryNode dir, FileInfo tmpFile)
        {
            DataSpaceMapUtils.AddDefaultDataSpace(dir);

            EncryptionInfo info = builder.GetInfo();

            //EncryptionRecord er = new EncryptionRecord(){
            //    public void Write(LittleEndianByteArrayOutputStream bos) {
            //        // EncryptionVersionInfo (4 bytes): A Version structure (section 2.1.4), where 
            //        // Version.vMajor MUST be 0x0004 and Version.vMinor MUST be 0x0004
            //        bos.Writeshort(info.VersionMajor);
            //        bos.Writeshort(info.VersionMinor);
            //        // Reserved (4 bytes): A value that MUST be 0x00000040
            //        bos.WriteInt(info.EncryptionFlags);

            //        EncryptionDocument ed = CreateEncryptionDocument();
            //        marshallEncryptionDocument(ed, bos);
            //    }
            //};

            //CreateEncryptionEntry(dir, "EncryptionInfo", er);
        }


        /**
         * 2.3.4.15 Data Encryption (Agile Encryption)
         * 
         * The EncryptedPackage stream (1) MUST be encrypted in 4096-byte segments to facilitate nearly
         * random access while allowing CBC modes to be used in the encryption Process.
         * The Initialization vector for the encryption process MUST be obtained by using the zero-based
         * segment number as a blockKey and the binary form of the KeyData.saltValue as specified in
         * section 2.3.4.12. The block number MUST be represented as a 32-bit unsigned integer.
         * Data blocks MUST then be encrypted by using the Initialization vector and the intermediate key
         * obtained by decrypting the encryptedKeyValue from a KeyEncryptor Contained within the
         * KeyEncryptors sequence as specified in section 2.3.4.10. The data block MUST be pAdded to
         * the next integral multiple of the KeyData.blockSize value. Any pAdding bytes can be used. Note
         * that the StreamSize field of the EncryptedPackage field specifies the number of bytes of
         * unencrypted data as specified in section 2.3.4.4.
         */
        private class AgileCipherOutputStream : ChunkedCipherOutputStream {
            ISecretKey skey;
            public AgileCipherOutputStream(DirectoryNode dir, IEncryptionInfoBuilder builder, ISecretKey skey, AgileEncryptor encryptor)
                    : base(dir, 4096, builder, encryptor)
            {
                this.builder = builder;
                this.skey = skey;
                this.encryptor = encryptor;
            }


            protected override Cipher InitCipherForBlock(Cipher existing, int block, bool lastChunk)
            {
                return AgileDecryptor.InitCipherForBlock(existing, block, lastChunk, builder, skey, Cipher.ENCRYPT_MODE);
            }


            protected override void CalculateChecksum(FileInfo fileOut, int oleStreamSize)
            {
                // integrityHMAC needs to be updated before the encryption document is Created
                ((AgileEncryptor)encryptor).UpdateIntegrityHMAC(fileOut, oleStreamSize);
            }


            protected override void CreateEncryptionInfoEntry(DirectoryNode dir, FileInfo tmpFile)
            {
                ((AgileEncryptor)encryptor).CreateEncryptionInfoEntry(dir, tmpFile);
            }
        }

    }
}
