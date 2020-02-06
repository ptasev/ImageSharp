// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

#if Windows_NT
using System.Security.Principal;
#endif
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Diagnostics.Windows;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;

namespace SixLabors.ImageSharp.Benchmarks
{
    public class Config : ManualConfig
    {
        public Config()
        {
            this.Add(MemoryDiagnoser.Default);

#if Windows_NT
            if (this.IsElevated)
            {
                this.Add(new NativeMemoryProfiler());
            }
#endif

        }

        public class ShortClr : Config
        {
            public ShortClr()
            {
                this.Add(
                    Job.Default.With(ClrRuntime.Net472).WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(3),
                    Job.Default.With(CoreRuntime.Core31).WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(3),
                    Job.Default.With(CoreRuntime.Core21).WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(3));
            }
        }

#if Windows_NT
        private bool IsElevated
        {
            get
            {
                return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
#endif
    }
}
