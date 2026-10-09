# Native numeric test fixtures

These tiny graphs exercise the existing float `[1,n]` input and probability-index-1 scoring.
Python is only a fixture authoring tool; running the tests needs the normal ONNX native package.

| Fixture | Bytes | SHA-256 |
|---|---:|---|
| `identity.onnx` | 130 | `691a2d6476544d32195258db2e889967b1b25964ba89c209363b40bc9593425a` |
| `cancellable-loop.onnx` | 473 | `8d27a5864193f0d90a225ef7b0ab2e39677b771a082c975878268ba6e912aa5a` |

Identity reproduces the independently verified 2026-10-09 native probe. The Loop performs one
billion additions and is terminated in flight by the adapter cancellation test. Both use opset13;
Identity uses IR8 and Loop uses IR10. Generated and checked with disposable Python3.13/onnx1.19.0.
The bounded test observes unfinished native work, cancels, then awaits its joined exit before
asserting that no score was written. This proves the library boundary, not cloud/image execution.

Run this source from the repository root to reproduce the fixtures and their hashes:

```python
from pathlib import Path
import hashlib
import onnx
from onnx import TensorProto as T, helper as h

target = Path('tests/ForgeMission.Mcl.Tests/Fixtures/onnx')
target.mkdir(parents=True, exist_ok=True)
numeric = h.make_tensor_value_info('input', T.FLOAT, [1, 2])
probabilities = h.make_tensor_value_info('probabilities', T.FLOAT, [1, 2])
body = h.make_graph([
    h.make_node('Identity', ['condition'], ['next_condition']),
    h.make_node('Add', ['carried', 'delta'], ['next_value']),
], 'bounded-cancellation-fixture', [
    h.make_tensor_value_info('iteration', T.INT64, []),
    h.make_tensor_value_info('condition', T.BOOL, []),
    h.make_tensor_value_info('carried', T.FLOAT, [1, 2]),
], [
    h.make_tensor_value_info('next_condition', T.BOOL, []),
    h.make_tensor_value_info('next_value', T.FLOAT, [1, 2]),
], [h.make_tensor('delta', T.FLOAT, [], [0.000001])])
graph = h.make_graph([
    h.make_node('Loop', ['iterations', 'keep_running', 'input'], ['probabilities'], body=body)
], 'cancellation-probe', [numeric], [probabilities], [
    h.make_tensor('iterations', T.INT64, [], [1_000_000_000]),
    h.make_tensor('keep_running', T.BOOL, [], [True]),
])
model = h.make_model(graph, producer_name='phase76-core-fixture', opset_imports=[h.make_opsetid('', 13)], ir_version=10)
onnx.checker.check_model(model)
onnx.save(model, target / 'cancellable-loop.onnx')
identity = h.make_model(h.make_graph([h.make_node('Identity', ['input'], ['probabilities'])],
    'phase76_probe', [numeric], [probabilities]), producer_name='phase76_probe',
    opset_imports=[h.make_opsetid('', 13)], ir_version=8)
onnx.checker.check_model(identity)
onnx.save(identity, target / 'identity.onnx')
for path in sorted(target.glob('*.onnx')):
    print(path.name, len(path.read_bytes()), hashlib.sha256(path.read_bytes()).hexdigest())
```
