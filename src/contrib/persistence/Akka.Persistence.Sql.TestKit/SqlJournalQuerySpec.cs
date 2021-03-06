﻿//-----------------------------------------------------------------------
// <copyright file="SqlJournalQuerySpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence.Sql.Common.Journal;
using Akka.Persistence.Sql.Common.Queries;
using Akka.Persistence.TCK;
using Xunit;
using Xunit.Abstractions;
using Query = Akka.Persistence.Sql.Common.Queries.Query;

namespace Akka.Persistence.Sql.TestKit
{
    public abstract class SqlJournalQuerySpec : PluginSpec
    {
        class TestTimestampProvider : ITimestampProvider
        {
            private static IDictionary<Tuple<string, long>, long> KnownEventTimestampMappings = new Dictionary<Tuple<string, long>, long>
            {
                {Tuple.Create("p-1", 1L), 111 },
                {Tuple.Create("p-1", 2L), 112 },
                {Tuple.Create("p-1", 3L), 113 },
                {Tuple.Create("p-2", 1L), 111 },
                {Tuple.Create("p-2", 2L), 121 },
                {Tuple.Create("p-3", 1L), 111 },
                {Tuple.Create("p-3", 2L), 311 },
            };

            public long GenerateTimestamp(IPersistentRepresentation message)
            {
                return KnownEventTimestampMappings[Tuple.Create(message.PersistenceId, message.SequenceNr)];
            }
        }

        public static string TimestampConfig(string plugin)
        {
            return plugin + ".timestamp-provider =\"" + typeof(TestTimestampProvider).FullName + ", Akka.Persistence.Sql.TestKit\"";
        }

        private static readonly IPersistentRepresentation[] Events =
        {
            new Persistent("a-1", 1, "p-1", "System.String"),
            new Persistent("a-2", 2, "p-1", "System.String"),
            new Persistent("a-3", 3, "p-1", "System.String"),
            new Persistent("a-4", 1, "p-2", "System.String"),
            new Persistent(5, 2, "p-2", "System.Int32"),
            new Persistent(6, 1, "p-3", "System.Int32"),
            new Persistent("a-7", 2, "p-3", "System.String")
        };

        public IActorRef JournalRef { get; protected set; }

        protected SqlJournalQuerySpec(Config config = null, string actorSystemName = null, ITestOutputHelper output = null)
            : base(config, actorSystemName, output)
        {
            JournalRef = Extension.JournalFor(null);
        }

        [Fact]
        public void Journal_queried_on_PersistenceIdRange_returns_events_for_particular_persistent_ids()
        {
            var query = new Common.Queries.Query(1, Hints.PersistenceIds(new[] { "p-1", "p-2" }));
            QueryAndExpectSuccess(query, Events[0], Events[1], Events[2], Events[3], Events[4]);
        }

        [Fact]
        public void Journal_queried_on_Manifest_returns_events_with_particular_manifest()
        {
            var query = new Common.Queries.Query(2, Hints.Manifest("System.Int32"));
            QueryAndExpectSuccess(query, Events[4], Events[5]);
        }

        [Fact]
        public void Journal_queried_on_Timestamp_returns_events_occurred_after_or_equal_From_value()
        {
            var query = new Common.Queries.Query(3, Hints.TimestampAfter(113));
            QueryAndExpectSuccess(query, Events[2], Events[4], Events[6]);
        }

        [Fact]
        public void Journal_queried_on_Timestamp_returns_events_occurred_before_To_value()
        {
            var query = new Common.Queries.Query(4, Hints.TimestampBefore(121));
            QueryAndExpectSuccess(query, Events[0], Events[1], Events[2], Events[3], Events[5]);
        }

        [Fact]
        public void Journal_queried_on_Timestamp_returns_events_occurred_between_both_range_values()
        {
            var query = new Common.Queries.Query(5, Hints.TimestampBetween(113, 311));
            QueryAndExpectSuccess(query, Events[2], Events[4]);
        }

        [Fact]
        public void Journal_queried_using_multiple_hints_should_apply_all_of_them()
        {
            var query = new Common.Queries.Query(6,
                Hints.TimestampBefore(113),
                Hints.PersistenceIds(new[] { "p-1", "p-2" }),
                Hints.Manifest("System.String"));

            QueryAndExpectSuccess(query, Events[0], Events[1], Events[3]);
        }

        protected void Initialize()
        {
            WriteEvents();
        }

        private void WriteEvents()
        {
            var probe = CreateTestProbe();
            var message = new WriteMessages(Events.Select(p => new AtomicWrite(p)), probe.Ref, ActorInstanceId);

            JournalRef.Tell(message);
            probe.ExpectMsg<WriteMessagesSuccessful>();
            foreach (var persistent in Events)
            {
                probe.ExpectMsg(new WriteMessageSuccess(persistent, ActorInstanceId));
            }
        }

        private void QueryAndExpectSuccess(Common.Queries.Query query, params IPersistentRepresentation[] events)
        {
            JournalRef.Tell(query, TestActor);

            foreach (var e in events)
            {
                ExpectMsg<QueryResponse>(q =>
                    q.QueryId == query.QueryId &&
                    q.Message.PersistenceId == e.PersistenceId &&
                    q.Message.SequenceNr == e.SequenceNr &&
                    q.Message.Manifest == e.Manifest &&
                    q.Message.IsDeleted == e.IsDeleted &&
                    Equals(q.Message.Payload, e.Payload));
            }

            ExpectMsg(new QuerySuccess(query.QueryId));
        }
    }
}