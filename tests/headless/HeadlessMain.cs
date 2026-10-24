public static class HeadlessMain
{
    public static int Main()
    {
        int core = AR2CoreRegressionTests.Run();
        int coordinator = CoordinatorRegressionTests.Run();
        return core == 0 && coordinator == 0 ? 0 : 1;
    }
}
