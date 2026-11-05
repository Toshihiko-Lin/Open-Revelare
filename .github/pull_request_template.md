## What changes and why / 改动及原因

<!-- Describe the problem and resulting behaviour in one or two sentences. -->

## Related issue / 关联 issue

<!-- Fixes #123, or None -->

## Validation / 自检

- [ ] `dotnet build -c Release -warnaserror` passes with zero warnings / 零警告
- [ ] `dotnet test -c Release` passes / 全绿
- [ ] Behaviour changes include regression tests explaining the failure they prevent / 行为改动有对应回归测试

## Golden baselines / 金标准基线

<!-- Keep the applicable option and remove the others. -->

- [ ] Unchanged: `Golden/*.txt` was not modified / 未修改基线
- [ ] Intentionally changed: explain why pixel output should change and whether the channel statistics match expectations / 有意修改，请说明原因和通道统计变化
- [ ] Unexpected change: investigate the regression before updating baselines / 非预期变化，先排查回归
