using Xunit;

// 集成测试会启动真实进程（部分测试通过环境变量控制子进程行为），
// 关闭并行执行以避免环境变量与端口/进程资源相互干扰。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
