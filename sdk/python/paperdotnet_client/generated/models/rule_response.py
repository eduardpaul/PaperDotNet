from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .action_definition import ActionDefinition
    from .rule_trigger import RuleTrigger

@dataclass
class RuleResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The actions property
    actions: Optional[list[ActionDefinition]] = None
    # The condition property
    condition: Optional[str] = None
    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The enabled property
    enabled: Optional[bool] = None
    # The id property
    id: Optional[UUID] = None
    # The name property
    name: Optional[str] = None
    # When a rule runs: `type` is `itemAdded`, `itemUpdated`, `itemDeleted` or an extension trigger;`list` and `contentType` narrow it by name; `changedFields` (updates) needs one of them to change.
    trigger: Optional[RuleTrigger] = None
    # The updatedAt property
    updated_at: Optional[datetime.datetime] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> RuleResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: RuleResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return RuleResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .action_definition import ActionDefinition
        from .rule_trigger import RuleTrigger

        from .action_definition import ActionDefinition
        from .rule_trigger import RuleTrigger

        fields: dict[str, Callable[[Any], None]] = {
            "actions": lambda n : setattr(self, 'actions', n.get_collection_of_object_values(ActionDefinition)),
            "condition": lambda n : setattr(self, 'condition', n.get_str_value()),
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "enabled": lambda n : setattr(self, 'enabled', n.get_bool_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "trigger": lambda n : setattr(self, 'trigger', n.get_object_value(RuleTrigger)),
            "updatedAt": lambda n : setattr(self, 'updated_at', n.get_datetime_value()),
            "workspaceId": lambda n : setattr(self, 'workspace_id', n.get_uuid_value()),
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
        writer.write_collection_of_object_values("actions", self.actions)
        writer.write_str_value("condition", self.condition)
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_bool_value("enabled", self.enabled)
        writer.write_uuid_value("id", self.id)
        writer.write_str_value("name", self.name)
        writer.write_object_value("trigger", self.trigger)
        writer.write_datetime_value("updatedAt", self.updated_at)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

