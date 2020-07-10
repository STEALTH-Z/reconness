using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using ReconNess.Core;
using ReconNess.Core.Models;
using ReconNess.Core.Services;
using ReconNess.Entities;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReconNess.Services
{
    /// <summary>
    /// This class implement <see cref="IAgentService"/>
    /// </summary>
    public class AgentService : Service<Agent>, IService<Agent>, IAgentService
    {
        private readonly IRootDomainService rootDomainService;
        private readonly IConnectorService connectorService;
        private readonly IScriptEngineService scriptEngineService;
        private readonly INotificationService notificationService;
        private readonly IRunnerProcess runnerProcess;

        /// <summary>
        /// Initializes a new instance of the <see cref="AgentService" /> class
        /// </summary>
        /// <param name="unitOfWork"><see cref="IUnitOfWork"/></param>
        /// <param name="rootDomainService"><see cref="IRootDomainService"/></param>
        /// <param name="connectorService"><see cref="IConnectorService"/></param>
        /// <param name="scriptEngineService"><see cref="IScriptEngineService"/></param>
        /// <param name="notificationService"><see cref="INotificationService"/></param>
        /// <param name="runnerProcess"><see cref="IRunnerProcess"/></param>
        public AgentService(IUnitOfWork unitOfWork,
            IRootDomainService rootDomainService,
            IConnectorService connectorService,
            IScriptEngineService scriptEngineService,
            INotificationService notificationService,
            IRunnerProcess runnerProcess)
            : base(unitOfWork)
        {
            this.rootDomainService = rootDomainService;
            this.connectorService = connectorService;
            this.scriptEngineService = scriptEngineService;
            this.notificationService = notificationService;
            this.runnerProcess = runnerProcess;
        }

        /// <summary>
        /// <see cref="IAgentService.GetAllAgentsWithCategoryAsync(CancellationToken)"/>
        /// </summary>
        public async Task<List<Agent>> GetAllAgentsWithCategoryAsync(CancellationToken cancellationToken = default)
        {
            var result = await this.GetAllQueryable(cancellationToken)
                .Include(n => n.AgentNotification)
                .Include(a => a.AgentCategories)
                .ThenInclude(c => c.Category)
                .ToListAsync();

            return result.OrderBy(a => a.AgentCategories.FirstOrDefault()?.Category?.Name).ToList();
        }

        /// <summary>
        /// <see cref="IAgentService.GetAgentWithCategoryAsync(Expression{Func{Agent, bool}}, CancellationToken)"/>
        /// </summary>
        public async Task<Agent> GetAgentWithCategoryAsync(Expression<Func<Agent, bool>> criteria, CancellationToken cancellationToken = default)
        {
            return await this.GetAllQueryableByCriteria(criteria, cancellationToken)
                .Include(n => n.AgentNotification)
                .Include(a => a.AgentCategories)
                .ThenInclude(c => c.Category)
                .FirstOrDefaultAsync();
        }

        /// <summary>
        /// <see cref="IAgentService.GetDefaultAgentsToInstallAsync(CancellationToken)"/>
        /// </summary>
        public async Task<List<AgentDefault>> GetDefaultAgentsToInstallAsync(CancellationToken cancellationToken = default)
        {
            var client = new RestClient("https://raw.githubusercontent.com/");
            var request = new RestRequest("/reconness/reconness-agents/master/default-agents.json");

            var response = await client.ExecuteGetAsync(request, cancellationToken);
            var defaultAgents = JsonConvert.DeserializeObject<AgentDefaultList>(response.Content);

            return defaultAgents.Agents;
        }

        /// <summary>
        /// <see cref="IAgentService.GetAgentScript(string, CancellationToken)"/>
        /// </summary>
        public async Task<string> GetAgentScript(string scriptUrl, CancellationToken cancellationToken)
        {
            var client = new RestClient(scriptUrl);
            var request = new RestRequest();

            var response = await client.ExecuteGetAsync(request, cancellationToken);

            return response.Content;
        }

        /// <summary>
        /// <see cref="IAgentService.RunAsync(Target, RootDomain, Subdomain, Agent, string, CancellationToken)"></see>
        /// </summary>
        public async Task RunAsync(Target target, RootDomain rootDomain, Subdomain subdomain, Agent agent, string command, bool activateNotification, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var channel = this.GetChannel(target, rootDomain, subdomain, agent);

            this.runnerProcess.Stopped = false;

            if (this.NeedToRunInEachSubdomain(subdomain, agent))
            {
                // wait 1 sec to avoid broke the frontend modal
                Thread.Sleep(1000);

                foreach (var sub in rootDomain.Subdomains.ToList())
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        this.runnerProcess.Stopped = true;
                        this.runnerProcess.KillProcess();

                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (this.runnerProcess.Stopped)
                    {
                        break;
                    }

                    var needToSkip = this.NeedToSkipSubdomain(agent, sub);
                    if (needToSkip)
                    {
                        await this.connectorService.SendAsync("logs_" + channel, $"Skip subdomain: {sub.Name}");
                        continue;
                    }

                    await this.RunAgentAsync(target, rootDomain, sub, agent, command, channel, activateNotification, cancellationToken);
                }
            }
            else
            {
                await this.RunAgentAsync(target, rootDomain, subdomain, agent, command, channel, activateNotification, cancellationToken);
            }

            await this.SendAgentDoneNotificationAsync(channel, agent, activateNotification, cancellationToken);

            // update the last time that we run this agent
            agent.LastRun = DateTime.Now;
            await this.UpdateAsync(agent, cancellationToken);
        }

        /// <summary>
        /// <see cref="IAgentService.StopAsync(Target, RootDomain, Subdomain, Agent, CancellationToken)"></see>
        /// </summary>
        public async Task StopAsync(Target target, RootDomain rootDomain, Subdomain subdomain, Agent agent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = subdomain == null ? $"{target.Name}_{rootDomain.Name}_{agent.Name}" : $"{target.Name}_{rootDomain.Name}_{subdomain.Name}_{agent.Name}";

            if (this.runnerProcess.IsRunning())
            {

                try
                {
                    this.runnerProcess.KillProcess();
                }
                catch (Exception ex)
                {
                    await this.connectorService.SendAsync(channel, ex.Message, cancellationToken);
                }
            }

            this.runnerProcess.Stopped = true;
            await this.connectorService.SendAsync(channel, "Agent stopped!", cancellationToken);
        }

        /// <summary>
        /// <see cref="IAgentService.DebugAsync(string, string, CancellationToken)"/>
        /// </summary>
        public async Task<ScriptOutput> DebugAsync(string terminalOutput, string script, CancellationToken cancellationToken = default)
        {
            return await this.scriptEngineService.ParseInputAsync(terminalOutput, 0, script);
        }

        /// <summary>
        /// Run the Agent
        /// </summary>
        /// <param name="target">The target</param>
        /// <param name="rootDomain"></param>
        /// <param name="subdomain"></param>
        /// <param name="agent"></param>
        /// <param name="command"></param>
        /// <param name="channel"></param>
        /// <param name="activateNotification"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task RunAgentAsync(Target target, RootDomain rootDomain, Subdomain subdomain, Agent agent, string command, string channel, bool activateNotification, CancellationToken cancellationToken)
        {
            var commandToRun = this.GetCommand(target, rootDomain, subdomain, agent, command);

            await this.connectorService.SendAsync("logs_" + channel, $"RUN: {command}");
            await this.RunBashAsync(rootDomain, subdomain, agent, commandToRun, channel, activateNotification, cancellationToken);
        }

        /// <summary>
        /// Check if we need to skip the subdomain and does not the agent in that subdomain
        /// </summary>
        /// <param name="agent"></param>
        /// <param name="subdomain"></param>
        /// <returns></returns>
        private bool NeedToSkipSubdomain(Agent agent, Subdomain subdomain)
        {
            var needToBeAlive = agent.OnlyIfIsAlive && (subdomain.IsAlive == null || !subdomain.IsAlive.Value);
            var needTohasHttpOpen = agent.OnlyIfHasHttpOpen && (subdomain.HasHttpOpen == null || !subdomain.HasHttpOpen.Value);
            var needToSkip = agent.SkipIfRanBefore && (!string.IsNullOrEmpty(subdomain.FromAgents) && subdomain.FromAgents.Contains(agent.Name));

            return needToBeAlive || needTohasHttpOpen || needToSkip;
        }

        /// <summary>
        /// Method to run a bash command
        /// </summary>
        /// <param name="channel">The channel to send the menssage</param>
        /// <param name="command">The command to run on bash</param>
        /// <returns>A Task</returns>
        private async Task RunBashAsync(RootDomain rootDomain, Subdomain subdomain, Agent agent, string command, string channel, bool activateNotification, CancellationToken cancellationToken)
        {
            try
            {
                this.runnerProcess.StartProcess(command);
                this.scriptEngineService.InintializeAgent(agent);

                int lineCount = 1;
                while (this.runnerProcess.IsRunning() && !this.runnerProcess.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var terminalLineOutput = this.runnerProcess.TerminalLineOutput();
                    var scriptOutput = await this.scriptEngineService.ParseInputAsync(terminalLineOutput, lineCount++);

                    await this.connectorService.SendAsync("logs_" + channel, $"Output #: {lineCount}");
                    await this.connectorService.SendAsync("logs_" + channel, $"Output: {terminalLineOutput}");
                    await this.connectorService.SendAsync("logs_" + channel, $"Result: {JsonConvert.SerializeObject(scriptOutput)}");

                    await this.rootDomainService.SaveScriptOutputAsync(rootDomain, subdomain, agent, scriptOutput, activateNotification, cancellationToken);

                    await this.connectorService.SendAsync("logs_" + channel, $"Output #: {lineCount} processed");
                    await this.connectorService.SendAsync("logs_" + channel, "-----------------------------------------------------");

                    await this.connectorService.SendAsync(channel, terminalLineOutput, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await SendLogException(channel, ex);
            }
            finally
            {
                this.runnerProcess.KillProcess();
            }
        }

        /// <summary>
        /// Obtain the channel to send the menssage
        /// </summary>
        /// <param name="rootDomain">The domain</param>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The agent</param>
        /// <returns>The channel to send the menssage</returns>
        private string GetChannel(Target target, RootDomain rootDomain, Subdomain subdomain, Agent agent)
        {
            return subdomain == null ? $"{target.Name}_{rootDomain.Name}_{agent.Name}" : $"{target.Name}_{rootDomain.Name}_{subdomain.Name}_{agent.Name}";
        }

        /// <summary>
        /// Obtain if we need to run this agent in each target subdomains base on if the subdomain
        /// param if null and the agent can run in the subdomain level
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The agent</param>
        /// <returns>If we need to run this agent in each target subdomains</returns>
        private bool NeedToRunInEachSubdomain(Subdomain subdomain, Agent agent)
        {
            return subdomain == null && agent.IsBySubdomain;
        }

        /// <summary>
        /// Obtain the command to run on bash
        /// </summary>
        /// <param name="target">The target</param>
        /// <param name="rootDomain">The domain</param>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The agent</param>
        /// <param name="command">The command to run</param>
        /// <returns>The command to run on bash</returns>
        private string GetCommand(Target target, RootDomain rootDomain, Subdomain subdomain, Agent agent, string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                command = agent.Command;
            }

            return $"{command.Replace("{{domain}}", subdomain == null ? rootDomain.Name : subdomain.Name)}"
                .Replace("{{target}}", target.Name)
                .Replace("{{rootDomain}}", rootDomain.Name)
                .Replace("\"", "\\\"");
        }

        /// <summary>
        /// Send a log message
        /// </summary>
        /// <param name="channel">The channel logs to send the menssage</param>
        /// <param name="ex">The Exception Object</param>
        /// <returns>Send a log message</returns>
        private async Task SendLogException(string channel, Exception ex)
        {
            await this.connectorService.SendAsync(channel, ex.Message);
            await this.connectorService.SendAsync("logs_" + channel, $"Exception: {ex.StackTrace}");
        }

        /// <summary>
        /// Send a msg and a notification when the agent finish
        /// </summary>
        /// <param name="agent">The agent</param>
        /// <param name="activateNotification">If we need to send a notification</param>
        /// <param name="channel">The channel to use to send the msg</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task SendAgentDoneNotificationAsync(string channel, Agent agent, bool activateNotification, CancellationToken cancellationToken)
        {
            if (activateNotification && agent.NotifyIfAgentDone)
            {
                await this.notificationService.SendAsync($"Agent {agent.Name} is done!", cancellationToken);
            }

            await this.connectorService.SendAsync(channel, "Agent done!", cancellationToken);
        }
    }
}
