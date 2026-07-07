// OpenLogi.Core.Action collides with System.Action (ImplicitUsings). Alias the
// domain type so the agent's binding/dispatch code reads cleanly.
global using Action = OpenLogi.Core.Actions.MouseAction;
