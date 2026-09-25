from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class ExtensionContributions(AdditionalDataHolder, Parsable):
    """
    What an extension contributes (for the catalog API and validation).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The automationActions property
    automation_actions: Optional[list[str]] = None
    # The automationTriggers property
    automation_triggers: Optional[list[str]] = None
    # The contentTypes property
    content_types: Optional[list[str]] = None
    # The extension's own DbContext (EXT-07), if any.
    db_context: Optional[str] = None
    # The endpoints property
    endpoints: Optional[bool] = None
    # The eventSubscribers property
    event_subscribers: Optional[list[str]] = None
    # The fieldTypes property
    field_types: Optional[list[str]] = None
    # The itemMutators property
    item_mutators: Optional[list[str]] = None
    # The jobs property
    jobs: Optional[list[str]] = None
    # The listTemplates property
    list_templates: Optional[list[str]] = None
    # The mcpTools property
    mcp_tools: Optional[list[str]] = None
    # The templateHandlers property
    template_handlers: Optional[list[str]] = None
    # The termSets property
    term_sets: Optional[list[str]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ExtensionContributions:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ExtensionContributions
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ExtensionContributions()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "automationActions": lambda n : setattr(self, 'automation_actions', n.get_collection_of_primitive_values(str)),
            "automationTriggers": lambda n : setattr(self, 'automation_triggers', n.get_collection_of_primitive_values(str)),
            "contentTypes": lambda n : setattr(self, 'content_types', n.get_collection_of_primitive_values(str)),
            "dbContext": lambda n : setattr(self, 'db_context', n.get_str_value()),
            "endpoints": lambda n : setattr(self, 'endpoints', n.get_bool_value()),
            "eventSubscribers": lambda n : setattr(self, 'event_subscribers', n.get_collection_of_primitive_values(str)),
            "fieldTypes": lambda n : setattr(self, 'field_types', n.get_collection_of_primitive_values(str)),
            "itemMutators": lambda n : setattr(self, 'item_mutators', n.get_collection_of_primitive_values(str)),
            "jobs": lambda n : setattr(self, 'jobs', n.get_collection_of_primitive_values(str)),
            "listTemplates": lambda n : setattr(self, 'list_templates', n.get_collection_of_primitive_values(str)),
            "mcpTools": lambda n : setattr(self, 'mcp_tools', n.get_collection_of_primitive_values(str)),
            "templateHandlers": lambda n : setattr(self, 'template_handlers', n.get_collection_of_primitive_values(str)),
            "termSets": lambda n : setattr(self, 'term_sets', n.get_collection_of_primitive_values(str)),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_collection_of_primitive_values("automationActions", self.automation_actions)
        writer.write_collection_of_primitive_values("automationTriggers", self.automation_triggers)
        writer.write_collection_of_primitive_values("contentTypes", self.content_types)
        writer.write_str_value("dbContext", self.db_context)
        writer.write_bool_value("endpoints", self.endpoints)
        writer.write_collection_of_primitive_values("eventSubscribers", self.event_subscribers)
        writer.write_collection_of_primitive_values("fieldTypes", self.field_types)
        writer.write_collection_of_primitive_values("itemMutators", self.item_mutators)
        writer.write_collection_of_primitive_values("jobs", self.jobs)
        writer.write_collection_of_primitive_values("listTemplates", self.list_templates)
        writer.write_collection_of_primitive_values("mcpTools", self.mcp_tools)
        writer.write_collection_of_primitive_values("templateHandlers", self.template_handlers)
        writer.write_collection_of_primitive_values("termSets", self.term_sets)
        writer.write_additional_data_value(self.additional_data)
    

