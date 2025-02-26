﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Build.Experimental.BuildCheck.Infrastructure.BuildCheckManagerProvider;

namespace Microsoft.Build.BuildCheck.UnitTests
{
    public class TaskInvocationCheckDataTests : IDisposable
    {
        internal sealed class TestCheck : Check
        {
            #region Check initialization

            public static CheckRule SupportedRule = new CheckRule("BC0000", "TestRule", "TestDescription", "TestMessage",
                new CheckConfiguration() { Severity = CheckResultSeverity.Warning });

            public override string FriendlyName => "MSBuild.TestCheck";

            public override IReadOnlyList<CheckRule> SupportedRules { get; } = [SupportedRule];

            public override void Initialize(ConfigurationContext configurationContext)
            { }

            public override void RegisterActions(IBuildCheckRegistrationContext registrationContext)
            {
                registrationContext.RegisterTaskInvocationAction(TaskInvocationAction);
            }

            #endregion

            /// <summary>
            /// Stores all TaskInvocationAnalysisData reported during the build.
            /// </summary>
            public List<TaskInvocationCheckData> CheckData = new();

            private void TaskInvocationAction(BuildCheckDataContext<TaskInvocationCheckData> context)
            {
                CheckData.Add(context.Data);
            }
        }

        private ITestOutputHelper _output;

        private static TestCheck? s_testCheck;

        public TaskInvocationCheckDataTests(ITestOutputHelper output)
        {
            _output = output;

            BuildCheckManager.s_testFactoriesPerDataSource =
            [
                // BuildCheckDataSource.EventArgs
                [
                    new ([TestCheck.SupportedRule.Id], true, () => s_testCheck = new TestCheck()),
                ],
                // BuildCheckDataSource.Execution
                [],
            ];

            s_testCheck?.CheckData.Clear();
        }

        public void Dispose()
        {
            BuildCheckManager.s_testFactoriesPerDataSource = null;
        }

        private void BuildProject(string taskInvocation)
        {
            using (var env = TestEnvironment.Create(_output))
            {
                var testProject = env.CreateTestProjectWithFiles($"<Project><Target Name=\"Build\">{taskInvocation}</Target></Project>");

                using (var buildManager = new BuildManager())
                {
                    var request = new BuildRequestData(testProject.ProjectFile, new Dictionary<string, string?>(), MSBuildConstants.CurrentToolsVersion, [], null, BuildRequestDataFlags.None);
                    var parameters = new BuildParameters
                    {
                        Loggers = [new MockLogger(_output)],
                        LogTaskInputs = true,
                        IsBuildCheckEnabled = true,
                        ShutdownInProcNodeOnBuildFinish = true,
                    };

                    var result = buildManager.Build(parameters, request);

                    result.OverallResult.ShouldBe(BuildResultCode.Success);
                }

                foreach (var data in s_testCheck.ShouldNotBeNull().CheckData)
                {
                    data.ProjectFilePath.ShouldBe(testProject.ProjectFile);
                    data.TaskInvocationLocation.Line.ShouldBeGreaterThan(0);
                    data.TaskInvocationLocation.Column.ShouldBeGreaterThan(0);
                }
            }
        }

        [Fact]
        public void ReportsSimpleTaskParameters()
        {
            BuildProject("<Message Text='Hello'/>");

            s_testCheck!.CheckData.Count.ShouldBe(1);
            var data = s_testCheck.CheckData[0];
            data.TaskName.ShouldBe("Message");
            data.Parameters.Count.ShouldBe(1);
            data.Parameters["Text"].IsOutput.ShouldBe(false);
            data.Parameters["Text"].Value.ShouldBe("Hello");
        }

        [Theory]
        [InlineData("<Output TaskParameter='CombinedPaths' ItemName='OutputDirectories' />")]
        [InlineData("<Output TaskParameter='CombinedPaths' PropertyName='OutputDirectories' />")]
        public void ReportsComplexTaskParameters(string outputElement)
        {
            BuildProject($"""
                <ItemGroup>
                  <TestItem Include='item1;item2'/>
                </ItemGroup>
                <CombinePath BasePath='base' Paths='@(TestItem)'>
                    {outputElement}
                </CombinePath>
            """);

            s_testCheck!.CheckData.Count.ShouldBe(1);
            var data = s_testCheck.CheckData[0];
            data.TaskName.ShouldBe("CombinePath");
            data.Parameters.Count.ShouldBe(3);

            data.Parameters["Paths"].IsOutput.ShouldBe(false);
            data.Parameters["Paths"].Value.ShouldBeAssignableTo(typeof(IList));
            IList listValue = (IList)data.Parameters["Paths"].Value!;
            listValue.Count.ShouldBe(2);
            listValue[0]!.ShouldBeAssignableTo(typeof(ITaskItem));
            listValue[1]!.ShouldBeAssignableTo(typeof(ITaskItem));
            ((ITaskItem)listValue[0]!).ItemSpec.ShouldBe("item1");
            ((ITaskItem)listValue[1]!).ItemSpec.ShouldBe("item2");
            data.Parameters["CombinedPaths"].IsOutput.ShouldBe(true);
            data.Parameters["CombinedPaths"].Value.ShouldNotBeNull();
        }

        [Fact]
        public void TaskParameterEnumeratesValues()
        {
            var parameter1 = MakeParameter("string");
            parameter1.EnumerateValues().SequenceEqual(["string"]).ShouldBeTrue();
            parameter1.EnumerateStringValues().SequenceEqual(["string"]).ShouldBeTrue();

            var parameter2 = MakeParameter(true);
            parameter2.EnumerateValues().SequenceEqual([true]);
            parameter2.EnumerateStringValues().SequenceEqual(["True"]).ShouldBeTrue();

            var item1 = new TaskItem("item1");
            var parameter3 = MakeParameter(item1);
            parameter3.EnumerateValues().SequenceEqual([item1]).ShouldBeTrue();
            parameter3.EnumerateStringValues().SequenceEqual(["item1"]).ShouldBeTrue();

            var array1 = new object[] { "string1", "string2" };
            var parameter4 = MakeParameter(array1);
            parameter4.EnumerateValues().SequenceEqual(array1).ShouldBeTrue();
            parameter4.EnumerateStringValues().SequenceEqual(array1).ShouldBeTrue();

            var item2 = new TaskItem("item2");
            var array2 = new ITaskItem[] { item1, item2 };
            var parameter5 = MakeParameter(array2);
            parameter5.EnumerateValues().SequenceEqual(array2).ShouldBeTrue();
            parameter5.EnumerateStringValues().SequenceEqual(["item1", "item2"]).ShouldBeTrue();

            static TaskInvocationCheckData.TaskParameter MakeParameter(object value)
                => new TaskInvocationCheckData.TaskParameter(value, IsOutput: false);
        }
    }
}
