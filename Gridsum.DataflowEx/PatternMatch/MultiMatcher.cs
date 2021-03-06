﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace Gridsum.DataflowEx.PatternMatch
{
    public class MultiMatchCondition<TInput> : IMatchCondition<TInput>
    {
        protected readonly IMatchCondition<TInput>[] m_matchConditions;

        public MultiMatchCondition(IMatchCondition<TInput>[] matchConditions)
        {
            m_matchConditions = matchConditions;
        }
        
        public bool Matches(TInput input)
        {
            return m_matchConditions.Any(matchCondition => matchCondition.Matches(input));
        }
    }

    //todo:  input-output caching
    //todo:? speed up exact string matching by using dictionary
    //todo:? speed up endswith/beginwith string matching by using tree
    public class MultiMatcher<TInput, TOutput> : MultiMatchCondition<TInput> where TOutput : IMatchable<TInput>
    {
        private readonly TOutput[] m_matchables;

        public MultiMatcher(TOutput[] matchables) : base(matchables.Select(o => o.Condition).ToArray())
        {
            m_matchables = matchables;
        }

        public bool TryMatch(TInput input, out TOutput matchable)
        {
            for (int i = 0; i < m_matchables.Length; i++)
            {
                if (m_matchables[i].Condition.Matches(input))
                {
                    matchable = m_matchables[i];
                    return true;
                }
            }

            matchable = default (TOutput);
            return false;
        }

        public TOutput Match(TInput input, Func<TOutput> defaultValueFactory)
        {
            TOutput output;
            return TryMatch(input, out output) ? output : defaultValueFactory();
        }

        public TOutput Match(TInput input, TOutput defaultValue)
        {
            TOutput output;
            return TryMatch(input, out output) ? output : defaultValue;
        }

        public IEnumerable<TOutput> MatchMultiple(TInput input)
        {
            for (int i = 0; i < m_matchables.Length; i++)
            {
                if (m_matchables[i].Condition.Matches(input))
                {
                    yield return m_matchables[i];
                }
            }
        }

        public TOutput this[int index]
        {
            get
            {
                return m_matchables[index];
            }
        }
    }
}
