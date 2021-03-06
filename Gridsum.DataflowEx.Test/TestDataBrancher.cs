﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Gridsum.DataflowEx.Exceptions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Gridsum.DataflowEx.Test
{
    [TestClass]
    public class TestDataBrancher
    {
        [TestMethod]
        public async Task TestDataBrancher1()
        {
            var random = new Random();
            var dataCopier = new DataBrancher<int>();

            int sum1 = 0;
            int sum2 = 0;

            var action1 = new ActionBlock<int>(i => sum1 = sum1 + i);
            var action2 = new ActionBlock<int>(i => sum2 = sum2 + i);

            dataCopier.OutputBlock.LinkTo(action1, new DataflowLinkOptions() {PropagateCompletion = true});
            dataCopier.CopiedOutputBlock.LinkTo(action2, new DataflowLinkOptions() {PropagateCompletion = true});

            for (int j = 0; j < 1000; j++)
            {
                dataCopier.InputBlock.Post((int) (random.NextDouble()*10000));
            }

            dataCopier.InputBlock.Complete();

            await TaskEx.AwaitableWhenAll(action1.Completion, action2.Completion);

            Console.WriteLine("sum1 = {0} , sum2 = {1}", sum1, sum2);
            Assert.AreEqual(sum1, sum2);
        }

        [TestMethod]
        public async Task TestDataBrancher2()
        {
            var random = new Random();
            var buffer = new BufferBlock<int>();
            var dataCopier = new DataBrancher<int>();

            int sum1 = 0;
            int sum2 = 0;

            var action1 = new ActionBlock<int>(i => sum1 = sum1 + i);
            var action2 = new ActionBlock<int>(i => sum2 = sum2 + i);

            buffer.LinkTo(dataCopier.InputBlock, new DataflowLinkOptions() { PropagateCompletion = true });
            dataCopier.OutputBlock.LinkTo(action1, new DataflowLinkOptions() { PropagateCompletion = true });
            dataCopier.CopiedOutputBlock.LinkTo(action2, new DataflowLinkOptions() { PropagateCompletion = true });

            for (int j = 0; j < 1000; j++)
            {
                buffer.Post((int)(random.NextDouble() * 10000));
            }

            buffer.Complete();

            await TaskEx.AwaitableWhenAll(action1.Completion, action2.Completion);

            Console.WriteLine("sum1 = {0} , sum2 = {1}", sum1, sum2);
            Assert.AreEqual(sum1, sum2);
        }
    }
}
