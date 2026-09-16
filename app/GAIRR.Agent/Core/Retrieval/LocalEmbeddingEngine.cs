using System.IO;
using System.Text;
using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GAIRR.Core;

/// <summary>本地 ONNX BERT 向量引擎：加载 bge-small-zh-v1.5 ONNX 模型并执行 Embed 推理，
/// 输出 L2 归一化 512 维向量；模型缺失或推理失败时由调用方降级为传统检索。</summary>
public sealed class LocalEmbeddingEngine : IDisposable
{
    /// <summary>模型文件路径（exe 同目录 models/bge-small-zh-v1.5.onnx）</summary>
    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "models", "bge-small-zh-v1.5.onnx");

    /// <summary>词表路径（exe 同目录 models/vocab.txt，BertTokenizer 需要）</summary>
    public static string VocabPath => Path.Combine(AppContext.BaseDirectory, "models", "vocab.txt");

    InferenceSession? _session;
    BertTokenizer? _tokenizer;
    readonly object _sync = new();

    // 进程级共享单例：ONNX 会话加载耗时（秒级），每次查询新建会严重拖慢 SmartSearch
    static LocalEmbeddingEngine? _shared;
    static readonly object _sharedSync = new();

    /// <summary>进程内共享实例（懒加载，不释放；模型加载一次后跨查询复用）</summary>
    public static LocalEmbeddingEngine Shared
    {
        get { lock (_sharedSync) return _shared ??= new LocalEmbeddingEngine(); }
    }

    /// <summary>是否可用（模型与词表存在）</summary>
    public bool IsAvailable => File.Exists(ModelPath) && File.Exists(VocabPath);

    /// <summary>确保会话与分词器已加载；返回 false 表示不可用</summary>
    public bool EnsureReady()
    {
        if (_session != null) return true;
        if (!IsAvailable) return false;
        lock (_sync)
        {
            if (_session != null) return true;
            try
            {
                _tokenizer = new BertTokenizer();
                using var sr = new StreamReader(VocabPath, Encoding.UTF8);
                _tokenizer.LoadVocabulary(sr, convertInputToLowercase: false,
                    unknownToken: "[UNK]", clsToken: "[CLS]", sepToken: "[SEP]", padToken: "[PAD]");
                _session = new InferenceSession(ModelPath);
                return true;
            }
            catch
            {
                _session = null;
                _tokenizer = null;
                return false;
            }
        }
    }

    /// <summary>对文本执行 embedding：均值池化 + L2 归一化，返回 512 维 float；失败返回 null。</summary>
    public float[]? Embed(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!EnsureReady() || _session == null || _tokenizer == null) return null;
        try
        {
            var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Encode(text, 512);
            var ids = inputIds.ToArray();
            var mask = attentionMask.ToArray();
            var types = tokenTypeIds.ToArray();

            using var idOrt = OrtValue.CreateTensorValueFromMemory(ids, new long[] { 1, ids.Length });
            using var maskOrt = OrtValue.CreateTensorValueFromMemory(mask, new long[] { 1, mask.Length });
            using var typeOrt = OrtValue.CreateTensorValueFromMemory(types, new long[] { 1, types.Length });

            var inputs = new Dictionary<string, OrtValue>
            {
                { "input_ids", idOrt },
                { "attention_mask", maskOrt },
                { "token_type_ids", typeOrt }
            };

            using var outputs = _session.Run(new RunOptions(), inputs, _session.OutputNames);
            var span = outputs[0].GetTensorDataAsSpan<float>();
            var seqLen = ids.Length;
            var hiddenSize = span.Length / seqLen;

            // 掩码均值池化（BGE 官方建议）：只对 attention_mask=1 的真实 token 求平均，
            // 避免 [PAD] 占位（隐藏态近零）稀释语义、拉低相近文本 cos 导致排序偏离主题
            var emb = new float[hiddenSize];
            int valid = 0;
            for (int i = 0; i < seqLen; i++)
            {
                if (mask[i] == 0) continue;
                valid++;
                for (int j = 0; j < hiddenSize; j++)
                    emb[j] += span[i * hiddenSize + j];
            }
            if (valid > 0)
                for (int j = 0; j < hiddenSize; j++)
                    emb[j] /= valid;

            // L2 归一化
            var norm = 0f;
            for (int j = 0; j < hiddenSize; j++) norm += emb[j] * emb[j];
            norm = (float)Math.Sqrt(norm);
            if (norm > 0f)
            {
                for (int j = 0; j < hiddenSize; j++) emb[j] /= norm;
            }
            return emb;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>释放 ONNX 会话与分词器资源</summary>
    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
