from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .automation_step import AutomationStep
    from .automation_trigger import AutomationTrigger

@dataclass
class AutomationRequest(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The enabled property
    enabled: Optional[bool] = True
    # The condition property
    condition: Optional[str] = None
    # The description property
    description: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # The steps property
    steps: Optional[list[AutomationStep]] = None
    # The trigger property
    trigger: Optional[AutomationTrigger] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> AutomationRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: AutomationRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return AutomationRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .automation_step import AutomationStep
        from .automation_trigger import AutomationTrigger

        from .automation_step import AutomationStep
        from .automation_trigger import AutomationTrigger

        fields: dict[str, Callable[[Any], None]] = {
            "condition": lambda n : setattr(self, 'condition', n.get_str_value()),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "enabled": lambda n : setattr(self, 'enabled', n.get_bool_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "steps": lambda n : setattr(self, 'steps', n.get_collection_of_object_values(AutomationStep)),
            "trigger": lambda n : setattr(self, 'trigger', n.get_object_value(AutomationTrigger)),
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
        writer.write_str_value("condition", self.condition)
        writer.write_str_value("description", self.description)
        writer.write_bool_value("enabled", self.enabled)
        writer.write_str_value("name", self.name)
        writer.write_collection_of_object_values("steps", self.steps)
        writer.write_object_value("trigger", self.trigger)
        writer.write_additional_data_value(self.additional_data)
    

