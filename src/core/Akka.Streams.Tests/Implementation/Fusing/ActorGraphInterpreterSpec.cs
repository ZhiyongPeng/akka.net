﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Streams;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Akka.Streams.Dsl;
using Akka.Streams.Implementation.Fusing;
using Akka.Streams.Stage;
using Akka.Streams.TestKit;
using Akka.Streams.TestKit.Tests;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Akka.Streams.Tests.Implementation.Fusing
{
    public class ActorGraphInterpreterSpec : AkkaSpec
    {
         private readonly ActorMaterializer _materializer;

        public ActorGraphInterpreterSpec(ITestOutputHelper output = null) : base(output)
        {
            _materializer = ActorMaterializer.Create(Sys);
        }

        [Fact]
        public async void ActorGraphInterpreter_should_be_able_to_interpret_a_simple_identity_graph_stage()
        {
            await this.AssertAllStagesStopped(async () =>
            {
                var identity = GraphStages.Identity<int>();

                var result = await Source.From(Enumerable.Range(1, 100))
                    .Via(identity)
                    .Grouped(200)
                    .RunWith(Sink.First<IEnumerable<int>>(), _materializer);

                result.Should().Equal(Enumerable.Range(1, 100));
            }, _materializer);
        }

        [Fact]
        public async void ActorGraphInterpreter_should_be_able_to_reuse_a_simple_identity_graph_stage()
        {
            await this.AssertAllStagesStopped(async () =>
            {
                var identity = GraphStages.Identity<int>();

                var result = await Source.From(Enumerable.Range(1, 100))
                    .Via(identity)
                    .Via(identity)
                    .Via(identity)
                    .Grouped(200)
                    .RunWith(Sink.First<IEnumerable<int>>(), _materializer);

                result.Should().Equal(Enumerable.Range(1, 100));
            }, _materializer);
        }

        [Fact]
        public async void ActorGraphInterpreter_should_be_able_to_interpret_a_simple_bidi_stage()
        {
            await this.AssertAllStagesStopped(async () =>
            {
                var identityBidi = new IdentityBidiGraphStage();
                var identity = BidiFlow.FromGraph(identityBidi).Join(Flow.Identity<int>().Map(x => x));

                var result = await Source.From(Enumerable.Range(1, 10))
                    .Via(identity)
                    .Grouped(100)
                    .RunWith(Sink.First<IEnumerable<int>>(), _materializer);

                result.Should().Equal(Enumerable.Range(1, 10));
            }, _materializer);
        }

        [Fact]
        public async void ActorGraphInterpreter_should_be_able_to_interpret_and_reuse_a_simple_bidi_stage()
        {
            await this.AssertAllStagesStopped(async () =>
            {
                var identityBidi = new IdentityBidiGraphStage();
                var identityBidiFlow = BidiFlow.FromGraph(identityBidi);
                var identity = identityBidiFlow.Atop(identityBidiFlow).Atop(identityBidiFlow).Join(Flow.Identity<int>().Map(x => x));

                var result = await Source.From(Enumerable.Range(1, 10))
                    .Via(identity)
                    .Grouped(100)
                    .RunWith(Sink.First<IEnumerable<int>>(), _materializer);

                result.Should().Equal(Enumerable.Range(1, 10));
            }, _materializer);
        }

        [Fact]
        public async void ActorGraphInterpreter_should_be_able_to_interpret_a_rotated_identity_bidi_stage()
        {
            await this.AssertAllStagesStopped(async () =>
            {
                var rotatedBidi = new RotatedIdentityBidiGraphStage();
                var takeAll = Flow.Identity<int>()
                    .Grouped(200)
                    .ToMaterialized(Sink.First<IEnumerable<int>>(), Keep.Right);

                var result = RunnableGraph<Tuple<Task<IEnumerable<int>>, Task<IEnumerable<int>>>>.FromGraph(
                    GraphDsl.Create(takeAll, takeAll, Keep.Both, (builder, shape1, shape2) =>
                    {
                        var bidi = builder.Add(rotatedBidi);
                        var source1 = builder.Add(Source.From(Enumerable.Range(1, 10)));
                        var source2 = builder.Add(Source.From(Enumerable.Range(1, 100)));

                        builder
                            .From(source1).To(bidi.Inlet1)
                            .To(shape2.Inlet).From(bidi.Outlet2)

                            .From(source2).To(bidi.Inlet2)
                            .To(shape1.Inlet).From(bidi.Outlet1);

                        return ClosedShape.Instance;
                    })).Run(_materializer);

                var f1 = await result.Item1;
                var f2 = await result.Item2;

                f1.Should().Equal(Enumerable.Range(1, 100));
                f2.Should().Equal(Enumerable.Range(1, 10));
            }, _materializer);
        }

        [Fact]
        public void ActorGraphInterpreter_should_be_able_to_report_errors_if_an_error_happens_for_an_already_completed_stage()
        {
            var failyStage = new FailyGraphStage();

            EventFilter.Exception<ArgumentException>(new Regex("Error in stage.*")).ExpectOne(async () =>
            {
                await Source.FromGraph(failyStage).RunWith(Sink.Ignore<int>(), _materializer);
            });
        }

        [Fact]
        public void ActorGraphInterpreter_should_be_able_to_properly_handle_case_where_a_stage_fails_before_subscription_happens()
        {
            // Fuzzing needs to be off, so that the failure can propagate to the output boundary
            // before the ExposedPublisher message.
            var noFuzzMaterializer = ActorMaterializer.Create(Sys,
                ActorMaterializerSettings.Create(Sys).WithFuzzingMode(false));
            this.AssertAllStagesStopped(() =>
            {

                var evilLatch = new CountdownEvent(1);

                // This is a somewhat tricky test setup. We need the following conditions to be met:
                //  - the stage should fail its output port before the ExposedPublisher message is processed
                //  - the enclosing actor (and therefore the stage) should be kept alive until a stray SubscribePending arrives
                //    that has been enqueued after ExposedPublisher message has been enqueued, but before it has been processed
                //
                // To achieve keeping alive the stage for long enough, we use an extra input and output port and instead
                // of failing the stage, we fail only the output port under test.
                //
                // To delay the startup long enough, so both ExposedPublisher and SubscribePending are enqueued, we use an evil
                // latch to delay the preStart() (which in turn delays the enclosing actor's preStart).
                var failyStage = new FailyInPreStartGraphStage(evilLatch);

                var downstream0 = TestSubscriber.CreateProbe<int>(this);
                var downstream1 = TestSubscriber.CreateProbe<int>(this);

                var upstream = TestPublisher.CreateProbe<int>(this);

                RunnableGraph<Unit>.FromGraph(GraphDsl.Create<ClosedShape, Unit>(b =>
                {
                    var faily = b.Add(failyStage);

                    b.From(Source.FromPublisher<int, Unit>(upstream)).To(faily.In);
                    b.From(faily.Out0).To(Sink.FromSubscriber<int, Unit>(downstream0));
                    b.From(faily.Out1).To(Sink.FromSubscriber<int, Unit>(downstream1));

                    return ClosedShape.Instance;
                })).Run(noFuzzMaterializer);

                evilLatch.Signal();
                var ex = downstream0.ExpectSubscriptionAndError();
                ex.Should().BeOfType<Utils.TE>();
                ex.Message.Should().Be("Test failure in PreStart");

                // if an NRE would happen due to unset exposedPublisher (see #19338), this would receive a failure instead
                // of the actual element
                downstream1.Request(1);
                upstream.SendNext(42);
                downstream1.ExpectNext(42);

                upstream.SendComplete();
                downstream1.ExpectComplete();
            }, noFuzzMaterializer);
        }

        public class IdentityBidiGraphStage : GraphStage<BidiShape<int, int, int, int>>
        {
            private class Logic : GraphStageLogic
            {
                public Logic(BidiShape<int, int, int, int> shape) : base(shape)
                {
                    SetHandler(shape.Inlet1,
                        onPush: () => Push(shape.Outlet1, Grab(shape.Inlet1)),
                        onUpstreamFinish: () => Complete(shape.Outlet1));

                    SetHandler(shape.Inlet2,
                        onPush: () => Push(shape.Outlet2, Grab(shape.Inlet2)),
                        onUpstreamFinish: () => Complete(shape.Outlet2));

                    SetHandler(shape.Outlet1,
                        onPull: () => Pull(shape.Inlet1),
                        onDownstreamFinish: () => Cancel(shape.Inlet1));

                    SetHandler(shape.Outlet2,
                        onPull: () => Pull(shape.Inlet2),
                        onDownstreamFinish: () => Cancel(shape.Inlet2));
                }
            }

            public Inlet<int> In1 { get; }
            public Inlet<int> In2 { get; }
            public Outlet<int> Out1 { get; }
            public Outlet<int> Out2 { get; }

            public IdentityBidiGraphStage()
            {
                In1 = new Inlet<int>("in1");
                In2 = new Inlet<int>("in2");
                Out1 = new Outlet<int>("out1");
                Out2 = new Outlet<int>("out2");
                Shape = new BidiShape<int, int, int, int>(In1, Out1, In2, Out2);
            }

            public override BidiShape<int, int, int, int> Shape { get; }

            protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
            {
                return new Logic(Shape);
            }

            public override string ToString()
            {
                return "IdentityBidi";
            }
        }

        /// <summary>
        /// This is a "rotated" identity BidiStage, as it loops back upstream elements
        /// to its upstream, and loops back downstream elements to its downstream.
        /// </summary>
        public class RotatedIdentityBidiGraphStage : GraphStage<BidiShape<int, int, int, int>>
        {
            private class Logic : GraphStageLogic
            {
                public Logic(BidiShape<int, int, int, int> shape) : base(shape)
                {
                    SetHandler(shape.Inlet1,
                        onPush: () => Push(shape.Outlet2, Grab(shape.Inlet1)),
                        onUpstreamFinish: () => Complete(shape.Outlet2));

                    SetHandler(shape.Inlet2,
                        onPush: () => Push(shape.Outlet1, Grab(shape.Inlet2)),
                        onUpstreamFinish: () => Complete(shape.Outlet1));

                    SetHandler(shape.Outlet1,
                        onPull: () => Pull(shape.Inlet2),
                        onDownstreamFinish: () => Cancel(shape.Inlet2));

                    SetHandler(shape.Outlet2,
                        onPull: () => Pull(shape.Inlet1),
                        onDownstreamFinish: () => Cancel(shape.Inlet1));
                }
            }

            public Inlet<int> In1 { get; }
            public Inlet<int> In2 { get; }
            public Outlet<int> Out1 { get; }
            public Outlet<int> Out2 { get; }

            public RotatedIdentityBidiGraphStage()
            {
                In1 = new Inlet<int>("in1");
                In2 = new Inlet<int>("in2");
                Out1 = new Outlet<int>("out1");
                Out2 = new Outlet<int>("out2");
                Shape = new BidiShape<int, int, int, int>(In1, Out1, In2, Out2);
            }

            public override BidiShape<int, int, int, int> Shape { get; }

            protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
            {
                return new Logic(Shape);
            }

            public override string ToString()
            {
                return "IdentityBidi";
            }
        }

        public class FailyGraphStage : GraphStage<SourceShape<int>>
        {
            private class Logic : GraphStageLogic
            {
                public Logic(SourceShape<int> shape) : base(shape)
                {
                    SetHandler(shape.Outlet,
                        onPull: () =>
                        {
                            CompleteStage();
                            // This cannot be propagated now since the stage is already closed
                            Push(shape.Outlet, -1);
                        });
                }
            }

            public Outlet<int> Out { get; }

            public FailyGraphStage()
            {
                Out = new Outlet<int>("test.out");
                Shape = new SourceShape<int>(Out);
            }

            public override SourceShape<int> Shape { get; }

            protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
            {
                return new Logic(Shape);
            }

            public override string ToString()
            {
                return "Faily";
            }
        }

        /// <summary>
        /// </summary>
        public class FailyInPreStartGraphStage : GraphStage<FanOutShape<int, int, int>>
        {
            private readonly CountdownEvent _evilLatch;

            private class Logic : GraphStageLogic
            {
                private readonly CountdownEvent _evilLatch;
                private readonly FanOutShape<int, int, int> _shape;

                public Logic(FanOutShape<int, int, int> shape, CountdownEvent evilLatch) : base(shape)
                {
                    _shape = shape;
                    _evilLatch = evilLatch;

                    SetHandler(shape.Out0, IgnoreTerminateOutput); // We fail in PreStart anyway
                    SetHandler(shape.Out1, IgnoreTerminateOutput); // We fail in PreStart anyway
                    PassAlong(shape.In, shape.Out1);
                }

                public override void PreStart()
                {
                    Pull(_shape.In);
                    _evilLatch.Wait(TimeSpan.FromSeconds(3));
                    Fail(_shape.Out0, new Utils.TE("Test failure in PreStart"));
                }
            }

            public Inlet<int> In { get; }
            public Outlet<int> Out0 { get; }
            public Outlet<int> Out1 { get; }

            public FailyInPreStartGraphStage(CountdownEvent evilLatch)
            {
                _evilLatch = evilLatch;
                In = new Inlet<int>("test.in");
                Out0 = new Outlet<int>("test.out0");
                Out1 = new Outlet<int>("test.out1");
                Shape = new FanOutShape<int, int, int>(In, Out0, Out1);
            }

            public override FanOutShape<int, int, int> Shape { get; }

            protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
            {
                return new Logic(Shape, _evilLatch);
            }

            public override string ToString()
            {
                return "Faily";
            }
        }
    }
}