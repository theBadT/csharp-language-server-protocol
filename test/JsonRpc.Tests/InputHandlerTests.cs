using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NSubstitute;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.JsonRpc.Server.Messages;
using Xunit;
using Request = OmniSharp.Extensions.JsonRpc.Server.Request;
using Response = OmniSharp.Extensions.JsonRpc.Client.Response;

namespace JsonRpc.Tests
{
    public class InputHandlerTests
    {
        private static InputHandler NewHandler(
            Stream inputStream,
            IOutputHandler outputHandler,
            IReciever reciever,
            IRequestProcessIdentifier requestProcessIdentifier,
            IRequestRouter requestRouter,
            IResponseRouter responseRouter,
            Action<CancellationTokenSource> action)
        {
            var cts = new CancellationTokenSource();
            if (!System.Diagnostics.Debugger.IsAttached)
                cts.CancelAfter(TimeSpan.FromSeconds(5));
            action(cts);

            var handler = new InputHandler(
                inputStream,
                outputHandler,
                reciever,
                requestProcessIdentifier,
                requestRouter,
                responseRouter,
                Substitute.For<ILoggerFactory>(),
                new Serializer());
            handler.Start();
            cts.Wait();
            Task.Delay(10).Wait();
            return handler;
        }

        [Fact]
        public void ShouldPassInRequests()
        {
            var inputStream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 2\r\n\r\n{}"));
            var outputHandler = Substitute.For<IOutputHandler>();
            var reciever = Substitute.For<IReciever>();

            using (NewHandler(
                inputStream,
                outputHandler,
                reciever,
                Substitute.For<IRequestProcessIdentifier>(),
                Substitute.For<IRequestRouter>(),
                Substitute.For<IResponseRouter>(),
                cts =>
                {
                    reciever.When(x => x.IsValid(Arg.Any<JToken>()))
                        .Do(x =>
                        {
                            cts.Cancel();
                        });
                }))
            {
                reciever.Received().IsValid(Arg.Is<JToken>(x => x.ToString() == "{}"));
            }
        }

        [Fact]
        public void ShouldHaveAThreadName()
        {
            var threadName = "(untouched)";
            var inputStream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 2\r\n\r\n{}"));
            var reciever = Substitute.For<IReciever>();

            using (NewHandler(
                inputStream,
                Substitute.For<IOutputHandler>(),
                reciever,
                Substitute.For<IRequestProcessIdentifier>(),
                Substitute.For<IRequestRouter>(),
                Substitute.For<IResponseRouter>(),
                cts =>
                {
                    reciever.When(x => x.IsValid(Arg.Any<JToken>()))
                        .Do(x =>
                        {
                            threadName = System.Threading.Thread.CurrentThread.Name;
                            cts.Cancel();
                        });
                }))
            {
                reciever.Received();
                threadName.Should().Be("ProcessInputStream", because: "it is easier to find it in the Threads pane by it's name");
            }

        }

        [Fact]
        public void ShouldPassInUtf8EncodedRequests()
        {
            // Note: an ä (&auml;) is encoded by two bytes, so string-length is 13 and byte-length is 14
            var inputStream = new MemoryStream(Encoding.UTF8.GetBytes("Content-Length: 14\r\n\r\n{\"utf8\": \"ä\"}"));
            var outputHandler = Substitute.For<IOutputHandler>();
            var reciever = Substitute.For<IReciever>();

            using (NewHandler(
                inputStream,
                outputHandler,
                reciever,
                Substitute.For<IRequestProcessIdentifier>(),
                Substitute.For<IRequestRouter>(),
                Substitute.For<IResponseRouter>(),
                cts =>
                {
                    reciever.When(x => x.IsValid(Arg.Any<JToken>()))
                        .Do(x =>
                        {
                            cts.Cancel();
                        });
                }))
            {
                reciever.Received().IsValid(Arg.Is<JToken>(x => x["utf8"].ToString() == "ä"));
            }
        }

        [Fact]
        public void ShouldHandleRequest()
        {
            var inputStream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 2\r\n\r\n{}"));
            var outputHandler = Substitute.For<IOutputHandler>();
            var reciever = Substitute.For<IReciever>();
            var incomingRequestRouter = Substitute.For<IRequestRouter>();

            var req = new Request(1, "abc", null);
            reciever.IsValid(Arg.Any<JToken>()).Returns(true);
            reciever.GetRequests(Arg.Any<JToken>())
                .Returns(c => (new Renor[] { req }, false));

            var response = new Response(1);

            incomingRequestRouter.RouteRequest(Arg.Any<IHandlerDescriptor>(), req)
                .Returns(response);

            using (NewHandler(
                inputStream,
                outputHandler,
                reciever,
                Substitute.For<IRequestProcessIdentifier>(),
                incomingRequestRouter,
                Substitute.For<IResponseRouter>(),
                cts =>
                {
                    outputHandler.When(x => x.Send(Arg.Any<object>()))
                        .Do(x =>
                        {
                            cts.Cancel();
                        });
                }))
            {
                outputHandler.Received().Send(Arg.Is<object>(x => x == response));
            }
        }

        [Fact]
        public void ShouldHandleError()
        {
            var inputStream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 2\r\n\r\n{}"));
            var outputHandler = Substitute.For<IOutputHandler>();
            var reciever = Substitute.For<IReciever>();
            var incomingRequestRouter = Substitute.For<IRequestRouter>();

            var error = new RpcError(1, new ErrorMessage(1, "abc"));
            reciever.IsValid(Arg.Any<JToken>()).Returns(true);
            reciever.GetRequests(Arg.Any<JToken>())
                .Returns(c => (new Renor[] { error }, false));


            using (NewHandler(
                inputStream,
                outputHandler,
                reciever,
                Substitute.For<IRequestProcessIdentifier>(),
                incomingRequestRouter,
                Substitute.For<IResponseRouter>(),
                cts =>
                {
                    outputHandler.When(x => x.Send(Arg.Any<object>()))
                        .Do(x =>
                        {
                            cts.Cancel();
                        });
                }))
            {
                outputHandler.Received().Send(Arg.Is<object>(x => x == error));
            }
        }

        [Fact]
        public async Task ShouldHandleNotification()
        {
            var inputStream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 2\r\n\r\n{}"));
            var outputHandler = Substitute.For<IOutputHandler>();
            var reciever = Substitute.For<IReciever>();
            var incomingRequestRouter = Substitute.For<IRequestRouter>();

            var notification = new Notification("abc", null);
            reciever.IsValid(Arg.Any<JToken>()).Returns(true);
            reciever.GetRequests(Arg.Any<JToken>())
                .Returns(c => (new Renor[] { notification }, false));

            using (NewHandler(
                inputStream,
                outputHandler,
                reciever,
                Substitute.For<IRequestProcessIdentifier>(),
                incomingRequestRouter,
                Substitute.For<IResponseRouter>(),
                cts =>
                {
                    incomingRequestRouter.When(x => x.RouteNotification(Arg.Any<IHandlerDescriptor>(), Arg.Any<Notification>()))
                        .Do(x =>
                        {
                            cts.Cancel();
                        });
                }))
            {
                await incomingRequestRouter.Received().RouteNotification(Arg.Any<IHandlerDescriptor>(), notification);
            }
        }

        [Fact]
        public void ShouldHandleResponse()
        {
            var inputStream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 2\r\n\r\n{}"));
            var outputHandler = Substitute.For<IOutputHandler>();
            var reciever = Substitute.For<IReciever>();
            var responseRouter = Substitute.For<IResponseRouter>();

            var response = new OmniSharp.Extensions.JsonRpc.Server.ServerResponse(1L, JToken.Parse("{}"));
            reciever.IsValid(Arg.Any<JToken>()).Returns(true);
            reciever.GetRequests(Arg.Any<JToken>())
                .Returns(c => (new Renor[] { response }, true));

            var tcs = new TaskCompletionSource<JToken>();
            responseRouter.GetRequest(1L).Returns(tcs);

            using (NewHandler(
                inputStream,
                outputHandler,
                reciever,
                Substitute.For<IRequestProcessIdentifier>(),
                Substitute.For<IRequestRouter>(),
                responseRouter,
                cts =>
                {
                    responseRouter.When(x => x.GetRequest(Arg.Any<long>()))
                        .Do(x =>
                        {
                            cts.CancelAfter(1);
                        });
                }))
            {
                responseRouter.Received().GetRequest(1L);
                tcs.Task.Result.ToString().Should().Be("{}");
            }
        }
    }
}
