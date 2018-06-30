using System;
using System.Composition.Hosting;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.LanguageServerProtocol.Eventing;
using OmniSharp.LanguageServerProtocol.Handlers;
using OmniSharp.Mef;
using OmniSharp.Models.Diagnostics;
using OmniSharp.Services;
using OmniSharp.Utilities;
using OmniSharp.Extensions.LanguageServer.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace OmniSharp.LanguageServerProtocol
{
    internal class LanguageServerHost : IDisposable
    {
        private readonly LanguageServerOptions _options;
        private CompositionHost _compositionHost;
        private readonly CommandLineApplication _application;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly LoggerFactory _loggerFactory;
        private IConfiguration _configuration;
        private IServiceProvider _serviceProvider;
        private RequestHandlers _handlers;
        private OmniSharpEnvironment _environment;
        private ILogger<LanguageServerHost> _logger;

        public LanguageServerHost(
            Stream input,
            Stream output,
            CommandLineApplication application,
            CancellationTokenSource cancellationTokenSource)
        {
            _options = new LanguageServerOptions();
            _options.WithLoggerFactory(_loggerFactory = new LoggerFactory())
                    .AddDefaultLoggingProvider()
                    .WithInput(input)
                    .WithOutput(output)
                    .OnInitialize(Initialize);
            _logger = _loggerFactory.CreateLogger<LanguageServerHost>();
            _application = application;
            _cancellationTokenSource = cancellationTokenSource;
        }

        public void Dispose()
        {
            _compositionHost?.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        private static LogLevel GetLogLevel(InitializeTrace initializeTrace)
        {
            switch (initializeTrace)
            {
                case InitializeTrace.Verbose:
                    return LogLevel.Trace;

                case InitializeTrace.Off:
                    return LogLevel.Warning;

                case InitializeTrace.Messages:
                default:
                    return LogLevel.Information;
            }
        }

        private void CreateCompositionHost(InitializeParams initializeParams)
        {
            _environment = new OmniSharpEnvironment(
                Helpers.FromUri(initializeParams.RootUri),
                Convert.ToInt32(initializeParams.ProcessId ?? -1L),
                GetLogLevel(initializeParams.Trace),
                _application.OtherArgs.ToArray());

            _configuration = new ConfigurationBuilder(_environment).Build();

            var eventEmitter = new LanguageServerEventEmitter();

            _serviceProvider = CompositionHostBuilder.CreateDefaultServiceProvider(_environment, _configuration, eventEmitter);

            var plugins = _application.CreatePluginAssemblies();

            var assemblyLoader = _serviceProvider.GetRequiredService<IAssemblyLoader>();
            var compositionHostBuilder = new CompositionHostBuilder(_serviceProvider)
                .WithOmniSharpAssemblies()
                .WithAssemblies(typeof(LanguageServerHost).Assembly)
                .WithAssemblies(assemblyLoader.LoadByAssemblyNameOrPath(plugins.AssemblyNames).ToArray());

            _compositionHost = compositionHostBuilder.Build();

            var projectSystems = _compositionHost.GetExports<IProjectSystem>();

            var documentSelectors = projectSystems
                .GroupBy(x => x.Language)
                .Select(x => (
                    language: x.Key,
                    selector: new DocumentSelector(x
                        .SelectMany(z => z.Extensions)
                        .Distinct()
                        .Select(z => new DocumentFilter()
                        {
                            Pattern = $"**/*{z}"
                        }))
                    ))
                    .ToArray();

            _logger.LogTrace(
                "Configured Document Selectors {@DocumentSelectors}",
                documentSelectors.Select(x => new { x.language, x.selector })
            );

            // TODO: Get these with metadata so we can attach languages
            // This will thne let us build up a better document filter, and add handles foreach type of handler
            // This will mean that we will have a strategy to create handlers from the interface type
            _handlers = new RequestHandlers(
                _compositionHost.GetExports<Lazy<IRequestHandler, OmniSharpRequestHandlerMetadata>>(),
                documentSelectors
            );

            _logger.LogTrace("--- Handler Definitions ---");
            foreach (var handlerCollection in _handlers)
            {
                foreach (var handler in handlerCollection)
                {
                    _logger.LogTrace(
                        "Handler: {Language}:{DocumentSelector}:{Handler}",
                        handlerCollection.Language,
                        handlerCollection.DocumentSelector.ToString(),
                        handler.GetType().FullName
                    );
                }
            }
            _logger.LogTrace("--- Handler Definitions ---");
        }

        private Task Initialize(InitializeParams initializeParams)
        {
            CreateCompositionHost(initializeParams);

            // TODO: Make it easier to resolve handlers from MEF (without having to add more attributes to the services if we can help it)
            var workspace = _compositionHost.GetExport<OmniSharpWorkspace>();

            foreach (var handler in TextDocumentSyncHandler.Enumerate(_handlers, workspace)
                .Concat(DefinitionHandler.Enumerate(_handlers))
                .Concat(HoverHandler.Enumerate(_handlers))
                .Concat(CompletionHandler.Enumerate(_handlers))
                .Concat(SignatureHelpHandler.Enumerate(_handlers))
                .Concat(RenameHandler.Enumerate(_handlers))
                .Concat(DocumentSymbolHandler.Enumerate(_handlers)))
            {
                _options.WithHandler(handler);
            }

            // _options.LogMessage(new LogMessageParams()
            // {
            //     Message = "Added handlers... waiting for initialize...",
            //     Type = MessageType.Log
            // });

            return Task.CompletedTask;
        }

        public async Task Start()
        {
            var server = await LanguageServer.From(_options);

            server.Window.LogMessage(new LogMessageParams()
            {
                Message = "initialized...",
                Type = MessageType.Log
            });

            var logger = _loggerFactory.CreateLogger(typeof(LanguageServerHost));
            WorkspaceInitializer.Initialize(_serviceProvider, _compositionHost, _configuration, logger);

            // Kick on diagnostics
            var diagnosticHandler = _handlers.GetAll()
                .OfType<Mef.IRequestHandler<DiagnosticsRequest, DiagnosticsResponse>>();

            foreach (var handler in diagnosticHandler)
                await handler.Handle(new DiagnosticsRequest());

            logger.LogInformation($"Omnisharp server running using Lsp at location '{_environment.TargetDirectory}' on host {_environment.HostProcessId}.");

            Console.CancelKeyPress += (sender, e) =>
            {
                _cancellationTokenSource.Cancel();
                e.Cancel = true;
            };

            if (_environment.HostProcessId != -1)
            {
                try
                {
                    var hostProcess = Process.GetProcessById(_environment.HostProcessId);
                    hostProcess.EnableRaisingEvents = true;
                    hostProcess.OnExit(() => _cancellationTokenSource.Cancel());
                }
                catch
                {
                    // If the process dies before we get here then request shutdown
                    // immediately
                    _cancellationTokenSource.Cancel();
                }
            }
        }
    }
}
