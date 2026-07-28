using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

public class Bip39ChecksumCalculator
{
    // 提示：实际开发中，这里需要载入完整的 2048 个 BIP-39 英文单词表
    // 可以从 raw.githubusercontent.com/bitcoin/bips/master/bip-0039/english.txt 获取
    private static readonly string[] WordList = LoadBip39WordList();

    public static List<string> GetValid12thWords(string[] first11Words)
    {
        if (first11Words.Length != 11)
            throw new ArgumentException("必须严格提供前 11 个单词");

        // 1. 将前 11 个单词转换为 121 位的二进制字符串
        StringBuilder bits121 = new StringBuilder(121);
        foreach (var word in first11Words)
        {
            int index = Array.IndexOf(WordList, word.ToLower());
            if (index == -1)
                throw new ArgumentException($"词库中不存在此单词: {word}");

            // 补齐 11 位二进制
            bits121.Append(Convert.ToString(index, 2).PadLeft(11, '0'));
        }

        List<string> valid12thWords = new List<string>();

        using (SHA256 sha256 = SHA256.Create())
        {
            // 2. 穷举缺失的最后 7 位熵的所有可能 (2^7 = 128 种可能)
            for (int i = 0; i < 128; i++)
            {
                // 生成这 7 位的二进制表示
                string bits7 = Convert.ToString(i, 2).PadLeft(7, '0');

                // 将 121 位与 7 位拼接，恢复出完整的 128 位熵
                string entropy128Bits = bits121.ToString() + bits7;

                // 3. 将 128 位的字符串转换回 16 字节的 byte 数组，准备进行哈希
                byte[] entropyBytes = new byte[16];
                for (int j = 0; j < 16; j++)
                {
                    string byteStr = entropy128Bits.Substring(j * 8, 8);
                    entropyBytes[j] = Convert.ToByte(byteStr, 2);
                }

                // 4. 对 128 位熵进行 SHA-256 哈希计算
                byte[] hash = sha256.ComputeHash(entropyBytes);

                // 5. 提取哈希结果第一个字节的前 4 位，作为校验和 (Checksum)
                string hashFirstByteBits = Convert.ToString(hash[0], 2).PadLeft(8, '0');
                string checksum4 = hashFirstByteBits.Substring(0, 4);

                // 6. 将 7 位熵与 4 位校验和拼接，这就是第 12 个单词的 11 位索引
                string word12Bits = bits7 + checksum4;
                int word12Index = Convert.ToInt32(word12Bits, 2);

                // 7. 查表得出合法的第 12 个单词
                valid12thWords.Add(WordList[word12Index]);
            }
        }

        return valid12thWords;
    }

    // 占位方法：模拟加载词库
    public static string[] LoadBip39WordList()
    {
        // 实际使用时，请读取你本地保存的 english.txt 并按行分割返回数组
        // 这里仅为了编译通过返回一个空数组，你需要替换它
        return File.ReadLines("./words.txt").ToArray();
       
    }
}