﻿using System.Collections.Generic;
using System.Linq;
using Easy.MessageHub;
using HomeAutio.Mqtt.GoogleHome.Models.State;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace HomeAutio.Mqtt.GoogleHome.Controllers
{
    /// <summary>
    /// Google home controller.
    /// </summary>
    [Route("/google/home")]
    public class GoogleHomeController : Controller
    {
        private readonly IConfiguration _config;
        private readonly IMessageHub _messageHub;
        private readonly DeviceConfiguration _deviceConfiguration;
        private readonly StateCache _stateCache;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="messageHub"></param>
        /// <param name="deviceConfiguration"></param>
        /// <param name="stateCache"></param>
        public GoogleHomeController(
            IConfiguration configuration,
            IMessageHub messageHub,
            DeviceConfiguration deviceConfiguration,
            StateCache stateCache)
        {
            _config = configuration;
            _messageHub = messageHub;
            _deviceConfiguration = deviceConfiguration;
            _stateCache = stateCache;
        }

        [HttpGet]
        public IActionResult Get()
        {
            return Ok(_deviceConfiguration);
        }

        [HttpGet]
        [Route("currentState")]
        public IActionResult GetCurrentState()
        {
            return Ok(_stateCache);
        }

        /// <summary>
        /// Post handler.
        /// </summary>
        /// <param name="request">Request.</param>
        /// <returns>Response.</returns>
        [HttpPost]
        public IActionResult Post([FromBody]Models.Request.Request request)
        {
            // Begin building Response 
            var response = new Models.Response.Response { RequestId = request.RequestId };

            // Smart Home Intents use a single object in inputs, containing the intent value, and a payload object with automation-specific objects.
            if (request.Inputs == null || request.Inputs.Count != 1)
            {
                response.Payload = new Models.Response.ErrorResponsePayload { ErrorCode = "protocolError" };
                return BadRequest(response);
            }

            var input = request.Inputs[0];
            switch (input)
            {
                case Models.Request.SyncIntent syncIntent:
                    response.Payload = HandleSyncIntent(syncIntent);

                    return Ok(response);
                    break;
                case Models.Request.QueryIntent queryIntent:
                    response.Payload = HandleQueryIntent(queryIntent);

                    return Ok(response);
                    break;
                case Models.Request.ExecuteIntent executeIntent:
                    response.Payload = HandleExecuteIntent(executeIntent);

                    return Ok(response);
                    break;
                case Models.Request.DisconnectIntent disconnectIntent:
                    // Do nothing?
                    return Ok();
                    break;
            }

            // No valid intents found, return error
            response.Payload = new Models.Response.ErrorResponsePayload { ErrorCode = "protocolError" };
            return BadRequest(response);
        }

        /// <summary>
        /// Handles a <see cref="Models.Request.ExecuteIntent"/>.
        /// </summary>
        /// <param name="intent">Intent to process.</param>
        /// <returns>A <see cref="Models.Response.ExecutionResponsePayload"/>.</returns>
        private Models.Response.ExecutionResponsePayload HandleExecuteIntent(Models.Request.ExecuteIntent intent)
        {
            var executionResponsePayload = new Models.Response.ExecutionResponsePayload();

            foreach (var command in intent.Payload.Commands)
            {
                // Convert command to a event to publish
                _messageHub.Publish(command);

                // Build response payload
                var commandResponse = new Models.Response.Command
                {
                    Status = Models.Response.CommandStatus.Success,
                    Ids = command.Devices.Select(x => x.Id).ToList(),
                    States = command.Execution.SelectMany(x => x.Params).ToDictionary(x => x.Key, x => x.Value)
                };

                // Generate states
                var states = new Dictionary<string, object>();
                foreach (var execution in command.Execution)
                {
                    // Handle camera stream commands
                    if (execution.Command == "action.devices.commands.GetCameraStream")
                    {
                        if (command.Devices.Count() == 1)
                        {
                            var trait = _deviceConfiguration[command.Devices[0].Id].Traits.FirstOrDefault();
                            if (trait != null)
                            {
                                foreach (var state in trait.State)
                                {
                                    states.Add(state.Key, state.Value);
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (var param in execution.Params)
                        {
                            // Handle remapping of Modes and Toggles
                            if (param.Key == "updateModeSettings")
                            {
                                states.Add("currentModeSettings", param.Value);
                            }
                            else if (param.Key == "updateToggleSettings")
                            {
                                states.Add("currentToggleSettings", param.Value);
                            }
                            else
                            {
                                states.Add(param.Key, param.Value);
                            }
                        }

                    }
                }

                commandResponse.States = states;

                executionResponsePayload.Commands.Add(commandResponse);
            }

            return executionResponsePayload;
        }

        /// <summary>
        /// Handles a <see cref="Models.Request.QueryIntent"/>.
        /// </summary>
        /// <param name="intent">Intent to process.</param>
        /// <returns>A <see cref="Models.Response.QueryResponsePayload"/>.</returns>
        private Models.Response.QueryResponsePayload HandleQueryIntent(Models.Request.QueryIntent intent)
        {
            var queryResponsePayload = new Models.Response.QueryResponsePayload
            {
                Devices = intent.Payload.Devices.ToDictionary(
                    x => x.Id,
                    x =>
                    {
                        var device = _deviceConfiguration[x.Id];
                        var paramaters = new Dictionary<string, object>();
                        foreach (var stateParam in device.Traits
                            .Where(trait => trait.Trait != "action.devices.traits.CameraStream")
                            .SelectMany(trait => trait.State))
                        {
                            var subStateParams = stateParam.Value as IDictionary<string, object>;
                            if (subStateParams != null)
                            {
                                var subParamDictionary = new Dictionary<string, object>();
                                foreach (var subStateParam in subStateParams)
                                {
                                    subParamDictionary.Add(subStateParam.Key, _stateCache[subStateParam.Value as string]);
                                }
                                paramaters.Add(stateParam.Key, subParamDictionary as IDictionary<string, object>);
                            }
                            else
                            {
                                var val = _stateCache[stateParam.Value as string];
                                paramaters.Add(stateParam.Key, _stateCache[stateParam.Value as string]);
                            }
                        }

                        return paramaters as IDictionary<string, object>;
                    })
            };

            return queryResponsePayload;
        }

        /// <summary>
        /// Handles a <see cref="Models.Request.SyncIntent"/>.
        /// </summary>
        /// <param name="intent">Intent to process.</param>
        /// <returns>A <see cref="Models.Response.SyncResponsePayload"/>.</returns>
        private Models.Response.SyncResponsePayload HandleSyncIntent(Models.Request.SyncIntent intent)
        {
            var syncResponsePayload = new Models.Response.SyncResponsePayload
            {
                AgentUserId = _config.GetValue<string>("googleHomeAgentUserId"),
                Devices = _deviceConfiguration.Values.Select(x => new Models.Response.Device
                {
                    Id = x.Id,
                    Type = x.Type,
                    RoomHint = x.RoomHint,
                    WillReportState = x.WillReportState,
                    Traits = x.Traits.Select(trait => trait.Trait).ToList(),
                    Attributes = x.Traits
                        .Where(trait => trait.Attributes != null)
                        .SelectMany(trait => trait.Attributes)
                        .ToDictionary(kv => kv.Key, kv => kv.Value),
                    Name = x.Name,
                    DeviceInfo = x.DeviceInfo,
                    CustomData = x.CustomData
                }).ToList()
            };

            return syncResponsePayload;
        }
    }
}
