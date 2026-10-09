using System.Runtime.CompilerServices;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ForgeMission.Core.Adapters;

public class OnnxExpertRunner : IExpertRunner
{
    public async Task<StepEnvelope> RunAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var features = ReadFeatures(expert, context);
        using var sessionOptions = new SessionOptions();
        using var runOptions = new RunOptions();
        using var registration = ct.Register(() =>
        {
            sessionOptions.SetLoadCancellationFlag(true);
            runOptions.Terminate = true;
        });
        float score;
        try
        {
            score = await Task.Run(() => Infer(expert, features, sessionOptions, runOptions), CancellationToken.None);
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        ct.ThrowIfCancellationRequested();
        context[expert.OutputKey] = (double)score;

        var threshold  = float.Parse(expert.Threshold);
        var exceeded   = score > threshold;
        // Only gate the pipeline when role:judge is explicitly set.
        // Without it, the score is written to the context bag for when() routing and the step always passes.
        var status = exceeded && expert.IsJudge ? "fail" : "pass";
        var reason = exceeded && expert.IsJudge
            ? $"Anomaly score {score:F4} exceeds threshold {threshold}"
            : null;

        return new StepEnvelope(score.ToString("F4"), status, reason);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var envelope = await RunAsync(expert, context, ct);
        yield return envelope.Text;
    }

    private static float[] ReadFeatures(ExpertDefinition expert, Dictionary<string, object> context)
    {
        var inputs = expert.Inputs ?? [];
        var features = new float[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var key = inputs[i];
            if (!context.TryGetValue(key, out var raw))
                throw new InvalidOperationException($"ONNX feature '{key}' not found in context. Ensure a prior step writes it.");
            features[i] = Convert.ToSingle(raw);
        }
        return features;
    }

    private static float Infer(ExpertDefinition expert, float[] features, SessionOptions options, RunOptions runOptions)
    {
        var tensor = new DenseTensor<float>(features, [1, features.Length]);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", tensor) };
        var path = Path.IsPathRooted(expert.Model) ? expert.Model
            : Path.GetFullPath(Path.Combine(expert.ExpertDirectory, expert.Model));
        using var session = new InferenceSession(path, options);
        using var results = session.Run(inputs, session.OutputNames, runOptions);
        var probabilities = results.FirstOrDefault(r => r.Name == "probabilities") ?? results.Last();
        var scores = probabilities.AsEnumerable<float>().ToArray();
        return scores.Length >= 2 ? scores[1] : scores[0];
    }


}
