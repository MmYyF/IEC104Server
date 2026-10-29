import io

path = "docs/代码分析报告.md"
marker = "### 10.18 本层尚未覆盖的部分"
section = """### 10.17 第十二轮：20 万点规模下的性能优化

前十一轮都在改"看得见的东西"。这一轮的起点是一句使用反馈：

> "这个工具日常使用至少会创建 100 台的从站设备，至少有 10w 的遥测 10w 遥信来做测试。
> 看看有没有性能优化的地方，防止卡死或者反应太慢。"

#### 一、先造一把能重复用的尺子

这个量级靠人手测不出来：点一次"生成"要等十几秒才知道卡不卡，而且每次重测都得重新铺点表。
所以先加了压测模式（`Views/ScaleBench.cs`）：

```
IEC104-Server.exe --bench-scale 100,2000[,输出目录]
```

十一段全部在 **UI 线程上同步跑**。这一条是口径，不是实现细节——
界面卡顿的本质就是 UI 线程被占住的时间，把操作丢到后台线程去"测得更快"，
量出来的就不再是用户感受了。铺点走"初始化数据"窗口的真实入口
（`InitDataViewModel.Generate()`），跟用户点按钮完全同一条路径。

#### 二、优化前：不是"慢"，是"死"

100 台 × 2000 点（20 万点）第一次跑完，用了 **5 分 37 秒**：

```
2.铺点 200,000 点用时 336,636 ms（平均 3366 ms/台，最慢 4392 ms/台）
10.内存：托管 3425.5 MB，进程私有 4532.6 MB
```

期间界面完全不动——用户对"5 分钟不动"的判定只有一种，就是卡死。

#### 三、六个真问题

| # | 位置 | 问题 | 量级 |
|---|---|---|---|
| 1 | `SlaveViewModel.FindPoint` | `Points.FirstOrDefault` 线性扫描，铺点时每点查一次重号 | 2 亿次比较 |
| 2 | `RebuildPointTabs` | 每加一个点就触发一次整表遍历 + 分类型集合复制 | 20 万次整表复制 |
| 3 | `MainWindowViewModel.Records` | 每条报文 `Insert(0)` 整体搬移 + 一次集合事件 | 450 万次引用搬移 |
| 4 | `SlaveStation.Points` | 每次访问都 `OrderBy().ToList()` | 每次总召都排序 |
| 5 | `AutoChangeService.Tick` | 每拍 `.ToList()` 复制 20 万引用 | 1.6 MB/拍 |
| 6 | `WorkspaceStore.Load` | 逐点 `AddPoint` → 逐点 Reset → 逐点整表遍历 | 20 万次整表遍历 |
| 7 | `TrafficViewModel.Rebuild` | `Clear()` + 3000 次 `Add` | 3001 次集合事件/刷新 |

前三个是"铺点 5 分钟"的直接原因，第 6、7 个是第十二轮才发现的（第 6 个会让
"打开工作区"直接打不开，第 7 个会让报文窗口开着时拖慢主界面）。

#### 四、三类改法

**① 把线性查找换成字典索引**——`Dictionary<int, PointViewModel>`，
点号 → 界面模型 O(1)。协议层 `SlaveStation.Points` 的排序结果同样加缓存，增删点时失效。

**② 把"逐条通知"换成"批量单次 Reset"**——新增 `RangeObservableCollection<T>`：

```csharp
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)   // 一次 Reset
    public void ReplaceAll(IEnumerable<T> items)
    public void RemoveRange(IEnumerable<T> items)
}
```

配套的是批量入口 `SlaveViewModel.AddPoints(IReadOnlyList<PointDraft>)`：
协议层登记 → 建界面模型 → 一次入表 → 一次通知。
单点 `AddPoint` 保留，内部走同一条批量路径。

**③ 把"实时刷新"换成"合并刷新"**——报文记录先进 `List` 缓冲，
150 ms 定时器到了再整体 `ReplaceAll`。`150 ms` 是刻意的取舍：
再短就退化成逐条刷新，再长用户会觉得"报文不实时"。没积压时定时器自动停表。

#### 五、结果

| 项目 | 优化前 | 优化后 |
|---|---:|---:|
| 铺点 20 万点 | 336,636 ms | **3,694 ms** |
| 报文灌入 1 万条 | 326 ms | **入缓冲 1 ms + 合并刷新 14 ms** |
| 自动变化 2 万点 ×5 拍 | 51 ms | 21 ms |
| 批量改值 2 万点 | 5 ms | 2 ms |
| 切换主题（刷 20 万点） | 47 ms | 21 ms |
| 托管内存 | 3,425.5 MB | **98.8 MB** |
| 工作区保存 / 载入 | — | 295 ms（50.5 MB）/ 624 ms |

铺点快 **91 倍**，内存降 **34 倍**。完整数据与逐项原理见
`docs/性能优化报告.md`。

#### 六、这一轮的教训：别报"不等于用户看到东西"的数字

改造后第一次压测，报文那一段报的是"4 ms（当前 0 条）"。
4 ms 只是"塞进缓冲区"的时间，`Records.Count` 还是 0，因为 150 ms 定时器没到。
**报一个用户根本看不到结果的耗时，等于没测。**
现在这一段拆成两段量（入缓冲 / 合并刷新），并且强制 `FlushRecords()` 之后再读数。

第二个教训：**改了刷新机制，就要给刷新机制加断言**。
`Records` 换了类型、刷新方式从"逐条"变成"批量 Reset"，报文监视窗口是靠这个
`Reset` 联动的。Reset 在监视窗口那边一旦被当成"清空"，窗口会一直空着——
而**截图看不出来**（截图那一刻可能刚好有一条）。所以新增两条断言（52 → 54）：

```
traffic-batch: 首铺=True(100) 批量追加=True(140) 单条追加=True(141) 上限截断=True(3000) => PASS
record-buffer: 入缓冲 2,000 条后集合未变=True Flush 后=2000(期望 2000) => PASS
```

#### 七、验收

- 压测 100 台 × 2000 点全部跑完，`_bench.log` 落盘；
- 线性度校验：20 台（4 万点）铺点 643 ms → 100 台（20 万点）3,694 ms，
  点数 ×5、时间 ×5.7，是线性量级；
- `--capture-ui` 回归 **54 PASS / 0 FAIL**，浅色与深色各一遍；
- 发布单文件 exe（自包含、45 MB）**脱运行时**（清掉 `DOTNET_ROOT`）再跑一遍：54/54；
- 版本号 1.6.0 → **1.7.0**，产物在 `D:\\01_File\\03_rea\\tools\\IEC104-Server\\IEC104-Server.exe`。

"""

text = io.open(path, encoding="utf-8").read()
assert text.count(marker) == 1, text.count(marker)
text = text.replace(marker, section + marker)
io.open(path, "w", encoding="utf-8", newline="").write(text)
print("inserted, lines =", text.count("\n") + 1)
