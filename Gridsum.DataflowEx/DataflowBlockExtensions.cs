﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Gridsum.DataflowEx.Exceptions;

namespace Gridsum.DataflowEx
{
    public static class DataflowBlockExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SafePost<TIn>(this ITargetBlock<TIn> target, TIn item, int interval = 200, int retryCount = 3)
        {
            bool posted = target.Post(item);
            if (posted) return;

            for (int i = 1; i <= retryCount; i++)
            {
                Thread.Sleep(interval * i);
                posted = target.Post(item);
                if (posted) return;
            }

            throw new PostToBlockFailedException("Safe post to " + Utils.GetFriendlyName(target.GetType()) + " failed");
        }

        public static int GetBufferCount(this IDataflowBlock block)
        {
            dynamic b = block;

            var blockGenericType = block.GetType().GetGenericTypeDefinition();
            if (blockGenericType == typeof(TransformBlock<,>) || blockGenericType == typeof(TransformManyBlock<,>))
            {
                return b.InputCount + b.OutputCount;
            }

            if (blockGenericType == typeof(ActionBlock<>))
            {
                return b.InputCount;
            }

            if (blockGenericType == typeof (BufferBlock<>))
            {
                return b.Count;
            }

            if (blockGenericType == typeof (BatchBlock<>))
            {
                return b.OutputCount*b.BatchSize;
            }

//            if (typeof(ISourceBlock<>).IsInstanceOfType(block))
//            {
//                return b.OutputCount;
//            }

            throw new ArgumentException("Fail to auto-detect buffer count of block: " + Utils.GetFriendlyName(block.GetType()), "block");
        }
    }
}
