// OpenLogi.Core.Action collides with System.Action (pulled in by ImplicitUsings).
// Alias the domain type so tests can write `Action` unambiguously.
global using Action = OpenLogi.Core.Action;
