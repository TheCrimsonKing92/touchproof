namespace SpikeFixture;

public interface IService { void Run(); }
public sealed class First : IService { public void Run() { } }
public sealed class Second : IService { void IService.Run() { } }
public sealed class Unrelated { public void Run() { } }
public static class StaticTarget { public static void Run() { } }
public static class StaticOther { public static void Run() { } }
public static class Callback { public static void Target() { } }
public static class DelegateSeam { public static void Invoke(Action seam) => seam(); }
public static class InheritedTarget { public static void Run() { } }
public sealed partial class PartialTarget { public void Run() => OtherPart(); }
