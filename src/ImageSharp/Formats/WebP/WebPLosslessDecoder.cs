// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;

using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;

namespace SixLabors.ImageSharp.Formats.WebP
{
    /// <summary>
    /// Decoder for lossless webp images.
    /// </summary>
    /// <remarks>
    /// The lossless specification can be found here:
    /// https://developers.google.com/speed/webp/docs/webp_lossless_bitstream_specification
    /// </remarks>
    internal sealed class WebPLosslessDecoder
    {
        private readonly Vp8LBitReader bitReader;

        private readonly int imageDataSize;

        public WebPLosslessDecoder(Stream stream, int imageDataSize)
        {
            this.bitReader = new Vp8LBitReader(stream);
            this.imageDataSize = imageDataSize;

            // TODO: implement decoding. For simulating the decoding: skipping the chunk size bytes.
            //stream.Skip(imageDataSize + 34); // TODO: Not sure why the additional data starts at offset +34 at the moment.
        }

        public void Decode<TPixel>(Buffer2D<TPixel> pixels, int width, int height)
            where TPixel : struct, IPixel<TPixel>
        {
            this.ReadTransformations();
            int xsize = 0, ysize = 0;
            this.ReadHuffmanCodes(xsize, ysize);
        }

        private void ReadHuffmanCodes(int xsize, int ysize)
        {
            int maxAlphabetSize = 0;
            int colorCacheBits = 0;
            int numHtreeGroups = 1;
            int numHtreeGroupsMax = 1;

            // Read color cache, if present.
            bool colorCachePresent = this.bitReader.ReadBit();
            if (colorCachePresent)
            {
                colorCacheBits = (int)this.bitReader.Read(4);
                int colorCacheSize = 1 << colorCacheBits;
                if (!(colorCacheBits >= 1 && colorCacheBits <= WebPConstants.MaxColorCacheBits))
                {
                    WebPThrowHelper.ThrowImageFormatException("Invalid color cache bits found");
                }
            }

            // Read the Huffman codes.
            // If the next bit is zero, there is only one meta Huffman code used everywhere in the image. No more data is stored.
            // If this bit is one, the image uses multiple meta Huffman codes. These meta Huffman codes are stored as an entropy image.
            bool isEntropyImage = this.bitReader.ReadBit();
            if (isEntropyImage)
            {
                uint huffmanPrecision = this.bitReader.Read(3) + 2;
                int huffmanXSize = SubSampleSize(xsize, (int)huffmanPrecision);
                int huffmanYSize = SubSampleSize(ysize, (int)huffmanPrecision);

                // TODO: decode entropy image
                return;
            }

            // Find maximum alphabet size for the htree group.
            for (int j = 0; j < WebPConstants.HuffmanCodesPerMetaCode; j++)
            {
                int alphabetSize = WebPConstants.kAlphabetSize[j];
                if (j == 0 && colorCacheBits > 0)
                {
                    alphabetSize += 1 << colorCacheBits;
                }

                if (maxAlphabetSize < alphabetSize)
                {
                    maxAlphabetSize = alphabetSize;
                }
            }

            // TODO: not sure about the correct tabelSize here. Harcoded for now.
            //int tableSize = kTableSize[colorCacheBits];
            int tableSize = 2970;
            var table = new HuffmanCode[numHtreeGroups * tableSize];
            for (int i = 0; i < numHtreeGroupsMax; i++)
            {
                int size;
                int totalSize = 0;
                int isTrivialLiteral = 1;
                int maxBits = 0;
                var codeLengths = new int[maxAlphabetSize];
                for (int j = 0; j < WebPConstants.HuffmanCodesPerMetaCode; j++)
                {
                    int alphabetSize = WebPConstants.kAlphabetSize[j];
                    if (j == 0 && colorCacheBits > 0)
                    {
                        if (j == 0 && colorCacheBits > 0)
                        {
                            alphabetSize += 1 << colorCacheBits;
                        }

                        size = this.ReadHuffmanCode(alphabetSize, codeLengths, table);
                        if (size is 0)
                        {
                            WebPThrowHelper.ThrowImageFormatException("Huffman table size is zero");
                        }
                    }
                }
            }
        }

        private int ReadHuffmanCode(int alphabetSize, int[] codeLengths, HuffmanCode[] table)
        {
            bool simpleCode = this.bitReader.ReadBit();
            if (simpleCode)
            {
                // (i) Simple Code Length Code.
                // This variant is used in the special case when only 1 or 2 Huffman code lengths are non - zero,
                // and are in the range of[0, 255].All other Huffman code lengths are implicitly zeros.

                // Read symbols, codes & code lengths directly.
                uint numSymbols = this.bitReader.Read(1) + 1;
                uint firstSymbolLenCode = this.bitReader.Read(1);

                // The first code is either 1 bit or 8 bit code.
                uint symbol = this.bitReader.Read((firstSymbolLenCode == 0) ? 1 : 8);
                codeLengths[symbol] = 1;

                // The second code (if present), is always 8 bit long.
                if (numSymbols == 2)
                {
                    symbol = this.bitReader.Read(8);
                    codeLengths[symbol] = 1;
                }
            }
            else
            {
                // (ii)Normal Code Length Code:
                // The code lengths of a Huffman code are read as follows: num_code_lengths specifies the number of code lengths;
                // the rest of the code lengths (according to the order in kCodeLengthCodeOrder) are zeros.
                var codeLengthCodeLengths = new int[WebPConstants.NumCodeLengthCodes];
                uint numCodes = this.bitReader.Read(4) + 4;
                if (numCodes > WebPConstants.NumCodeLengthCodes)
                {
                    WebPThrowHelper.ThrowImageFormatException("Bitstream error, numCodes has an invalid value");
                }

                for (int i = 0; i < numCodes; i++)
                {
                    codeLengthCodeLengths[WebPConstants.KCodeLengthCodeOrder[i]] = (int)this.bitReader.Read(3);
                }

                this.ReadHuffmanCodeLengths(table, codeLengthCodeLengths, alphabetSize, codeLengths);
            }

            int size = 0;
            // TODO: VP8LBuildHuffmanTable

            return size;
        }

        private void ReadHuffmanCodeLengths(HuffmanCode[] table, int[] codeLengthCodeLengths, int numSymbols, int[] codeLengths)
        {
            int maxSymbol;
            int symbol = 0;
            int prevCodeLen = WebPConstants.DefaultCodeLength;
            int size = HuffmanUtils.BuildHuffmanTable(table, WebPConstants.LengthTableBits, codeLengthCodeLengths, WebPConstants.NumCodeLengthCodes);
            if (size is 0)
            {
                WebPThrowHelper.ThrowImageFormatException("Error building huffman table");
            }

            if (this.bitReader.ReadBit())
            {
                int lengthNBits = 2 + (2 * (int)this.bitReader.Read(3));
                maxSymbol = 2 + (int)this.bitReader.Read(lengthNBits);
            }
            else
            {
                maxSymbol = numSymbols;
            }

            while (symbol < numSymbols)
            {
                int codeLen;
                if (maxSymbol-- == 0)
                {
                    break;
                }

                codeLen = int.MaxValue; // TODO: this is wrong
                if (codeLen < WebPConstants.kCodeLengthLiterals)
                {
                    codeLengths[symbol++] = codeLen;
                    if (codeLen != 0)
                    {
                        prevCodeLen = codeLen;
                    }
                }
                else
                {
                    bool usePrev = codeLen == WebPConstants.kCodeLengthRepeatCode;
                    int slot = codeLen - WebPConstants.kCodeLengthLiterals;
                    int extraBits = WebPConstants.kCodeLengthExtraBits[slot];
                    int repeatOffset = WebPConstants.kCodeLengthRepeatOffsets[slot];
                    int repeat = (int)(this.bitReader.Read(extraBits) + repeatOffset);
                    if (symbol + repeat > numSymbols)
                    {
                        return;
                    }
                    else
                    {
                        int length = usePrev ? prevCodeLen : 0;
                        while (repeat-- > 0)
                        {
                            codeLengths[symbol++] = length;
                        }
                    }
                }
            }
        }

        private void ReadTransformations()
        {
            // Next bit indicates, if a transformation is present.
            bool transformPresent = this.bitReader.ReadBit();
            int numberOfTransformsPresent = 0;
            var transforms = new List<WebPTransformType>(WebPConstants.MaxNumberOfTransforms);
            while (transformPresent)
            {
                var transformType = (WebPTransformType)this.bitReader.Read(2);
                transforms.Add(transformType);
                switch (transformType)
                {
                    case WebPTransformType.SubtractGreen:
                        // There is no data associated with this transform.
                        break;
                    case WebPTransformType.ColorIndexingTransform:
                        // The transform data contains color table size and the entries in the color table.
                        // 8 bit value for color table size.
                        uint colorTableSize = this.bitReader.Read(8) + 1;

                        // TODO: color table should follow here?
                        break;

                    case WebPTransformType.PredictorTransform:
                        {
                            // The first 3 bits of prediction data define the block width and height in number of bits.
                            // The number of block columns, block_xsize, is used in indexing two-dimensionally.
                            uint sizeBits = this.bitReader.Read(3) + 2;
                            int blockWidth = 1 << (int)sizeBits;
                            int blockHeight = 1 << (int)sizeBits;

                            break;
                        }

                    case WebPTransformType.ColorTransform:
                        {
                            // The first 3 bits of the color transform data contain the width and height of the image block in number of bits,
                            // just like the predictor transform:
                            uint sizeBits = this.bitReader.Read(3) + 2;
                            int blockWidth = 1 << (int)sizeBits;
                            int blockHeight = 1 << (int)sizeBits;
                            break;
                        }
                }

                numberOfTransformsPresent++;

                transformPresent = this.bitReader.ReadBit();
                if (numberOfTransformsPresent == WebPConstants.MaxNumberOfTransforms && transformPresent)
                {
                    WebPThrowHelper.ThrowImageFormatException("The maximum number of transforms was exceeded");
                }
            }

            // TODO: return transformation in an appropriate form.
        }

        /// <summary>
        /// Computes sampled size of 'size' when sampling using 'sampling bits'.
        /// </summary>
        private int SubSampleSize(int size, int samplingBits)
        {
            return (size + (1 << samplingBits) - 1) >> samplingBits;
        }
    }
}
